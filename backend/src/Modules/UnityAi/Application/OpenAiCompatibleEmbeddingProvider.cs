using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Platform.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// OpenAI-compatible <see cref="IUnityAiEmbeddingProvider"/>. Mirrors
/// <see cref="OpenAiCompatibleAiProvider"/>'s shape: reuses the same
/// runtime config snapshot (provider name + base URL + API key), the
/// same IHttpClientFactory pattern, the same URL-normalization heuristic
/// (re-pointed at <c>/v1/embeddings</c> instead of <c>/chat/completions</c>).
///
/// Default model is <c>text-embedding-3-small</c> — 1536-dim output that
/// matches our pgvector column, ~$0.02/1M tokens, fast enough for
/// inline-per-search use even when the cache misses.
///
/// Never throws. All failure modes surface as a
/// <see cref="UnityAiEmbeddingOutcome"/> on the return value so the
/// search hot path can degrade silently.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProvider : IUnityAiEmbeddingProvider
{
    private const string DefaultEmbeddingModel = "text-embedding-3-small";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly Regex VersionTailRegex = new(@"/v\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IPlatformAiProviderRuntimeResolver _resolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiCompatibleEmbeddingProvider> _logger;

    public OpenAiCompatibleEmbeddingProvider(
        IPlatformAiProviderRuntimeResolver resolver,
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAiCompatibleEmbeddingProvider> logger)
    {
        _resolver = resolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => PlatformAiProviderKeys.OpenAi;

    public async Task<UnityAiEmbeddingResult> EmbedAsync(
        UnityAiEmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Inputs is null || request.Inputs.Count == 0)
        {
            return Result(UnityAiEmbeddingOutcome.Skipped, "Empty input batch.");
        }

        var snapshot = _resolver.GetCurrent()
            ?? await _resolver.RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (snapshot is null
            || string.Equals(snapshot.Provider, PlatformAiProviderKeys.Disabled, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(snapshot.ApiKey))
        {
            return Result(UnityAiEmbeddingOutcome.Disabled, "AI provider not configured (no API key or disabled).");
        }

        if (!string.Equals(snapshot.Provider, PlatformAiProviderKeys.OpenAi, StringComparison.OrdinalIgnoreCase))
        {
            return Result(UnityAiEmbeddingOutcome.Skipped,
                $"OpenAI-compatible embedding adapter received task routed for provider '{snapshot.Provider}'.");
        }

        var endpoint = ResolveEmbeddingsUrl(snapshot.BaseUrl);
        var modelToUse = !string.IsNullOrWhiteSpace(request.Model) ? request.Model! : DefaultEmbeddingModel;

        var http = _httpClientFactory.CreateClient(nameof(OpenAiCompatibleEmbeddingProvider));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var body = new
        {
            model = modelToUse,
            input = request.Inputs,
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.ApiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(httpRequest, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(UnityAiEmbeddingOutcome.Failed,
                $"Embedding call timed out (model={modelToUse}, endpoint={endpoint}).");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "OpenAI-compatible embedding provider network failure (endpoint={Endpoint})", endpoint);
            return Result(UnityAiEmbeddingOutcome.Failed,
                $"Network error reaching {endpoint}: {ex.Message}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = await ReadShortSnippetAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            return Result(UnityAiEmbeddingOutcome.Failed,
                $"Provider returned {(int)response.StatusCode} {response.ReasonPhrase} from {endpoint}. Body: {snippet}");
        }

        EmbeddingsResponse? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<EmbeddingsResponse>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken: timeoutCts.Token).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return Result(UnityAiEmbeddingOutcome.Failed,
                $"Could not parse provider response as OpenAI embeddings JSON: {ex.Message}");
        }

        if (payload?.Data is null || payload.Data.Count == 0)
        {
            return Result(UnityAiEmbeddingOutcome.Failed, "Provider returned no embedding data.");
        }

        if (payload.Data.Count != request.Inputs.Count)
        {
            return Result(UnityAiEmbeddingOutcome.Failed,
                $"Provider returned {payload.Data.Count} embeddings for {request.Inputs.Count} inputs.");
        }

        sw.Stop();
        // Sort by index so the result aligns with the input order — the
        // API contract returns the items in order but defending the
        // alignment is cheap and avoids a sub-grade bug if the provider
        // ever reorders.
        var ordered = new float[payload.Data.Count][];
        foreach (var item in payload.Data)
        {
            if (item.Index < 0 || item.Index >= ordered.Length || item.Embedding is null)
            {
                return Result(UnityAiEmbeddingOutcome.Failed, "Provider returned malformed embedding data.");
            }
            ordered[item.Index] = item.Embedding;
        }

        return new UnityAiEmbeddingResult(
            Outcome: UnityAiEmbeddingOutcome.Succeeded,
            Embeddings: ordered,
            Provider: PlatformAiProviderKeys.OpenAi,
            Model: payload.Model ?? modelToUse,
            TokenInputCount: payload.Usage?.PromptTokens,
            EstimatedCost: null,
            LatencyMs: (int)sw.ElapsedMilliseconds,
            ErrorMessage: null);
    }

    private static UnityAiEmbeddingResult Result(UnityAiEmbeddingOutcome outcome, string reason) => new(
        Outcome: outcome,
        Embeddings: Array.Empty<float[]>(),
        Provider: PlatformAiProviderKeys.OpenAi,
        Model: null,
        TokenInputCount: 0,
        EstimatedCost: 0m,
        LatencyMs: 0,
        ErrorMessage: reason);

    private static string ResolveEmbeddingsUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "https://api.openai.com/v1/embeddings";
        }

        var root = baseUrl!.Trim().TrimEnd('/');
        if (root.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }
        if (VersionTailRegex.IsMatch(root))
        {
            return $"{root}/embeddings";
        }
        return $"{root}/v1/embeddings";
    }

    private static async Task<string> ReadShortSnippetAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            return read == 0 ? "(empty)" : System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch
        {
            return "(unreadable)";
        }
    }

    /// <summary>Wire shape of <c>POST /v1/embeddings</c> response.</summary>
    private sealed record EmbeddingsResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("data")] List<EmbeddingItem>? Data,
        [property: JsonPropertyName("usage")] EmbeddingUsage? Usage);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[]? Embedding);

    private sealed record EmbeddingUsage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("total_tokens")] int? TotalTokens);
}
