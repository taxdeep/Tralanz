using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Platform.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Real <see cref="IUnityAiProvider"/> backed by the OpenAI
/// <c>/chat/completions</c> shape. Works with every OpenAI-compatible
/// backend in production today — OpenAI itself, plus DeepSeek, Together,
/// BigModel/智谱, OpenRouter, LM Studio, Ollama-OpenAI-shim, etc. — by
/// reusing the same URL-normalization heuristic the SysAdmin connection
/// probe uses (<see cref="HttpPlatformAiProviderProbe"/>).
///
/// Reads provider/baseUrl/model + decrypted API key from
/// <see cref="IPlatformAiProviderRuntimeResolver"/>. Returns
/// <see cref="UnityAiTaskOutcome.Skipped"/> when the SysAdmin row is not
/// configured or its API key is empty — the gateway then logs the call
/// (with provider="openai") and the deterministic fallback continues.
///
/// Structured output is requested via <c>response_format: { type:
/// "json_object" }</c>. Backends that don't support that flag will
/// usually still return JSON because the prompt itself instructs them to;
/// validation happens upstream in <see cref="UnityAiGateway"/>.
/// </summary>
public sealed class OpenAiCompatibleAiProvider : IUnityAiProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly Regex VersionTailRegex = new(@"/v\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IPlatformAiProviderRuntimeResolver _resolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiCompatibleAiProvider> _logger;

    public OpenAiCompatibleAiProvider(
        IPlatformAiProviderRuntimeResolver resolver,
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAiCompatibleAiProvider> logger)
    {
        _resolver = resolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Stable name the model router emits in <see cref="AiModelSelection.Provider"/>
    /// to pick this adapter. Matches <see cref="PlatformAiProviderKeys.OpenAi"/>.
    /// </summary>
    public string Name => PlatformAiProviderKeys.OpenAi;

    public bool Supports(string taskType, string capability) => true;

    public async Task<AiResponse> CompleteStructuredAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var snapshot = _resolver.GetCurrent()
            ?? await _resolver.RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (snapshot is null
            || string.Equals(snapshot.Provider, PlatformAiProviderKeys.Disabled, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(snapshot.ApiKey))
        {
            return SkippedResponse("AI provider not configured (no API key or disabled).");
        }

        if (!string.Equals(snapshot.Provider, PlatformAiProviderKeys.OpenAi, StringComparison.OrdinalIgnoreCase))
        {
            // The router shouldn't have routed an Anthropic / Azure call here.
            // Surface the mismatch loudly so it's debuggable.
            return SkippedResponse(
                $"OpenAI-compatible adapter received task routed for provider '{snapshot.Provider}'.");
        }

        var endpoint = ResolveChatCompletionsUrl(snapshot.BaseUrl);
        var modelToUse = !string.IsNullOrWhiteSpace(request.Model)
            ? request.Model!
            : (string.IsNullOrWhiteSpace(snapshot.Model) ? "gpt-4o-mini" : snapshot.Model);

        var http = _httpClientFactory.CreateClient(nameof(OpenAiCompatibleAiProvider));
        // The gateway also wraps each call in a Stopwatch — don't double-time.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.TimeoutMs is { } t and > 0 ? TimeSpan.FromMilliseconds(t) : RequestTimeout);

        var maxTokens = request.MaxOutputTokens ?? snapshot.MaxTokens;
        var temperature = snapshot.Temperature;

        var body = new
        {
            model = modelToUse,
            max_tokens = maxTokens,
            temperature = temperature,
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt },
            },
            response_format = new { type = "json_object" },
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
            return FailedResponse($"Provider call timed out (model={modelToUse}, endpoint={endpoint}).");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "OpenAI-compatible provider network failure (endpoint={Endpoint})", endpoint);
            return FailedResponse($"Network error reaching {endpoint}: {ex.Message}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Best-effort: drain a short snippet of the body so the
            // operator can see what went wrong instead of just a status code.
            var snippet = await ReadShortSnippetAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            return FailedResponse($"Provider returned {(int)response.StatusCode} {response.ReasonPhrase} from {endpoint}. Body: {snippet}");
        }

        OpenAiChatCompletionsResponse? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<OpenAiChatCompletionsResponse>(
                stream,
                cancellationToken: timeoutCts.Token).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return FailedResponse($"Could not parse provider response as OpenAI chat-completions JSON: {ex.Message}");
        }

        if (payload?.Choices is null || payload.Choices.Count == 0)
        {
            return FailedResponse("Provider returned no choices.");
        }

        var content = payload.Choices[0].Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return FailedResponse("Provider returned an empty message.content.");
        }

        return new AiResponse(
            Outcome: UnityAiTaskOutcome.Succeeded,
            OutputJson: content!.Trim(),
            TokenInputCount: payload.Usage?.PromptTokens,
            TokenOutputCount: payload.Usage?.CompletionTokens,
            EstimatedCost: null,
            LatencyMs: null,
            ErrorMessage: null);
    }

    private static AiResponse SkippedResponse(string reason) => new(
        Outcome: UnityAiTaskOutcome.Skipped,
        OutputJson: null,
        TokenInputCount: 0,
        TokenOutputCount: 0,
        EstimatedCost: 0m,
        LatencyMs: 0,
        ErrorMessage: reason);

    private static AiResponse FailedResponse(string error) => new(
        Outcome: UnityAiTaskOutcome.Failed,
        OutputJson: null,
        TokenInputCount: null,
        TokenOutputCount: null,
        EstimatedCost: null,
        LatencyMs: null,
        ErrorMessage: error);

    /// <summary>
    /// Same heuristic as <c>HttpPlatformAiProviderProbe.ResolveOpenAiChatCompletionsUrl</c>:
    /// already-complete URLs are used as-is, version-suffixed roots get
    /// <c>/chat/completions</c> appended, bare hosts get the OpenAI default.
    /// </summary>
    private static string ResolveChatCompletionsUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "https://api.openai.com/v1/chat/completions";
        }

        var root = baseUrl!.Trim().TrimEnd('/');
        if (root.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        if (VersionTailRegex.IsMatch(root))
        {
            return $"{root}/chat/completions";
        }

        return $"{root}/v1/chat/completions";
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

    // Minimal contract for OpenAI chat-completions response. Tolerant to
    // upstream additions because System.Text.Json silently ignores
    // unknown fields.
    private sealed class OpenAiChatCompletionsResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("choices")]
        public List<OpenAiChoice>? Choices { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("usage")]
        public OpenAiUsage? Usage { get; set; }
    }

    private sealed class OpenAiChoice
    {
        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public OpenAiMessage? Message { get; set; }
    }

    private sealed class OpenAiMessage
    {
        [System.Text.Json.Serialization.JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class OpenAiUsage
    {
        [System.Text.Json.Serialization.JsonPropertyName("prompt_tokens")]
        public int? PromptTokens { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("completion_tokens")]
        public int? CompletionTokens { get; set; }
    }
}
