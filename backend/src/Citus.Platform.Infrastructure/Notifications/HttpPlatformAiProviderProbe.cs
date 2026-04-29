using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Citus.Platform.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Citus.Platform.Infrastructure.Notifications;

/// <summary>
/// HttpClient-based "is this API key working?" probe. Sends a minimal
/// chat-completion request to each provider:
///
///   OpenAI       — POST .../chat/completions with max_tokens=1 ("hi")
///   Anthropic    — POST /v1/messages with max_tokens=1 ("hi")
///   Azure OpenAI — GET /openai/models?api-version=... (no model invocation)
///
/// The OpenAI probe used to GET /v1/models for a free auth-only ping,
/// but real-world setups often point the OpenAI provider at an OpenAI-
/// COMPATIBLE backend (DeepSeek, Together, BigModel/智谱, OpenRouter,
/// LM Studio, etc.) — many of which implement /chat/completions but
/// not /models. Switching the probe to /chat/completions makes the
/// "OpenAI" provider work for every compat backend in exchange for a
/// fractional-cent cost per Test Connection click.
/// </summary>
public sealed class HttpPlatformAiProviderProbe : IPlatformAiProviderProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpPlatformAiProviderProbe> _logger;

    public HttpPlatformAiProviderProbe(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpPlatformAiProviderProbe> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<PlatformAiProbeResult> ProbeAsync(
        string provider,
        string? baseUrl,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new PlatformAiProbeResult(false, null, "API key is empty.", null);
        }

        using var http = _httpClientFactory.CreateClient(nameof(HttpPlatformAiProviderProbe));
        http.Timeout = ProbeTimeout;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            return provider.Trim().ToLowerInvariant() switch
            {
                PlatformAiProviderKeys.OpenAi =>
                    await ProbeOpenAiAsync(http, baseUrl, model, apiKey, stopwatch, cancellationToken),
                PlatformAiProviderKeys.Anthropic =>
                    await ProbeAnthropicAsync(http, apiKey, model, stopwatch, cancellationToken),
                PlatformAiProviderKeys.AzureOpenAi =>
                    await ProbeAzureOpenAiAsync(http, baseUrl, apiKey, stopwatch, cancellationToken),
                PlatformAiProviderKeys.Disabled =>
                    new PlatformAiProbeResult(false, null,
                        "Provider is disabled — choose OpenAI, Anthropic, or Azure OpenAI to test.",
                        stopwatch.Elapsed),
                _ =>
                    new PlatformAiProbeResult(false, null,
                        $"Unsupported provider '{provider}'.", stopwatch.Elapsed),
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PlatformAiProbeResult(
                false, null, "Probe timed out after 15 seconds.", stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "AI probe network failure for {Provider}.", provider);
            return new PlatformAiProbeResult(
                false, null,
                $"Network error reaching provider: {ex.Message}",
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI probe unexpected failure for {Provider}.", provider);
            return new PlatformAiProbeResult(
                false, null, $"Unexpected error: {ex.Message}", stopwatch.Elapsed);
        }
    }

    private static async Task<PlatformAiProbeResult> ProbeOpenAiAsync(
        HttpClient http,
        string? baseUrl,
        string? model,
        string apiKey,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var probeUrl = ResolveOpenAiChatCompletionsUrl(baseUrl);
        var modelToProbe = string.IsNullOrWhiteSpace(model)
            ? "gpt-4o-mini"
            : model!.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, probeUrl)
        {
            Content = JsonContent.Create(new
            {
                model = modelToProbe,
                max_tokens = 1,
                messages = new[]
                {
                    new { role = "user", content = "hi" }
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await http.SendAsync(request, cancellationToken);
        stopwatch.Stop();

        return InterpretAuthStatus(response, stopwatch.Elapsed,
            successMessage: $"Authenticated to OpenAI-compatible endpoint ({(int)response.StatusCode}).",
            probedUrl: $"{probeUrl} (model={modelToProbe})");
    }

    /// <summary>
    /// Resolve the actual <c>/chat/completions</c> URL given whatever
    /// the operator pasted into the Base URL field. Common shapes:
    /// <list type="bullet">
    ///   <item>(empty) → https://api.openai.com/v1/chat/completions</item>
    ///   <item>https://api.openai.com → +/v1/chat/completions</item>
    ///   <item>https://api.openai.com/v1 → +/chat/completions</item>
    ///   <item>https://api.openai.com/v1/chat/completions → use as-is</item>
    ///   <item>https://open.bigmodel.cn/api/paas/v4 → +/chat/completions</item>
    ///   <item>https://open.bigmodel.cn/api/paas/v4/chat/completions → use as-is</item>
    /// </list>
    /// Heuristic: if the URL already ends with <c>/chat/completions</c>,
    /// it's a complete endpoint — use it. Otherwise look for a trailing
    /// version segment (<c>/v1</c>, <c>/v4</c>, …); if present, append
    /// <c>/chat/completions</c>; if absent, fall back to the OpenAI
    /// default of <c>/v1/chat/completions</c>.
    /// </summary>
    private static string ResolveOpenAiChatCompletionsUrl(string? baseUrl)
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

        // Trailing version segment? Match /v<digits> at the end.
        if (System.Text.RegularExpressions.Regex.IsMatch(root, @"/v\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return $"{root}/chat/completions";
        }

        // Bare host (no version segment) — assume OpenAI shape.
        return $"{root}/v1/chat/completions";
    }

    private static async Task<PlatformAiProbeResult> ProbeAnthropicAsync(
        HttpClient http,
        string apiKey,
        string model,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var modelToProbe = string.IsNullOrWhiteSpace(model)
            ? "claude-3-5-haiku-latest"
            : model.Trim();
        const string probeUrl = "https://api.anthropic.com/v1/messages";

        using var request = new HttpRequestMessage(HttpMethod.Post, probeUrl)
        {
            Content = JsonContent.Create(new
            {
                model = modelToProbe,
                max_tokens = 1,
                messages = new[]
                {
                    new { role = "user", content = "hi" }
                }
            })
        };
        request.Headers.Add("x-api-key", apiKey.Trim());
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await http.SendAsync(request, cancellationToken);
        stopwatch.Stop();

        return InterpretAuthStatus(response, stopwatch.Elapsed,
            successMessage: $"Authenticated to Anthropic ({(int)response.StatusCode}).",
            probedUrl: $"{probeUrl} (model={modelToProbe})");
    }

    private static async Task<PlatformAiProbeResult> ProbeAzureOpenAiAsync(
        HttpClient http,
        string? baseUrl,
        string apiKey,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new PlatformAiProbeResult(
                false, null,
                "Azure OpenAI requires a base URL (https://<resource>.openai.azure.com).",
                null);
        }

        // Same trailing-segment forgiveness as OpenAI: a trailing /openai
        // copy-pasted from the Azure portal would compose into
        // /openai/openai/models otherwise.
        var rootUrl = NormalizeAzureOpenAiBaseUrl(baseUrl);
        var probeUrl = $"{rootUrl}/openai/models?api-version=2024-02-01";
        using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
        request.Headers.Add("api-key", apiKey.Trim());

        using var response = await http.SendAsync(request, cancellationToken);
        stopwatch.Stop();

        return InterpretAuthStatus(response, stopwatch.Elapsed,
            successMessage: $"Authenticated to Azure OpenAI ({(int)response.StatusCode}).",
            probedUrl: probeUrl);
    }

    private static string NormalizeAzureOpenAiBaseUrl(string baseUrl)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        if (root.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^7];
        }
        return root;
    }

    private static PlatformAiProbeResult InterpretAuthStatus(
        HttpResponseMessage response,
        TimeSpan elapsed,
        string successMessage,
        string probedUrl)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return new PlatformAiProbeResult(true, status, successMessage, elapsed);
        }

        // Always include the URL we actually hit on failure — saves the
        // operator from guessing whether their base URL composed into
        // something double-prefixed (/v1/v1/, /openai/openai/, etc).
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                new PlatformAiProbeResult(false, status,
                    $"API key rejected (401 Unauthorized). Hit: {probedUrl}", elapsed),
            HttpStatusCode.Forbidden =>
                new PlatformAiProbeResult(false, status,
                    $"API key lacks permission (403 Forbidden). Hit: {probedUrl}", elapsed),
            HttpStatusCode.NotFound =>
                new PlatformAiProbeResult(false, status,
                    $"Endpoint or model not found (404). Hit: {probedUrl}. Check the base URL (no trailing /v1 or /openai needed) and the model name.", elapsed),
            HttpStatusCode.TooManyRequests =>
                new PlatformAiProbeResult(false, status,
                    "Rate-limited (429). Key looks valid, retry shortly.", elapsed),
            _ =>
                new PlatformAiProbeResult(false, status,
                    $"Provider returned {status} {response.ReasonPhrase}. Hit: {probedUrl}", elapsed),
        };
    }
}
