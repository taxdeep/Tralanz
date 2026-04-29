using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Citus.Platform.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Citus.Platform.Infrastructure.Notifications;

/// <summary>
/// HttpClient-based "is this API key working?" probe. Hits each
/// provider's cheapest auth-only endpoint:
///   OpenAI       — GET /v1/models
///   Anthropic    — POST /v1/messages with max_tokens=1 ("hi")
///   Azure OpenAI — GET /openai/models?api-version=...
///
/// Anthropic doesn't expose a free auth-only endpoint, so the probe
/// pays for one minimal completion. Token cost ≈ negligible (≤ a few
/// cents).
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
                    await ProbeOpenAiAsync(http, baseUrl, apiKey, stopwatch, cancellationToken),
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
        string apiKey,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var rootUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com" : baseUrl!.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{rootUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await http.SendAsync(request, cancellationToken);
        stopwatch.Stop();

        return InterpretAuthStatus(response, stopwatch.Elapsed,
            successMessage: $"Authenticated to OpenAI ({(int)response.StatusCode}).");
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

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
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
            successMessage: $"Authenticated to Anthropic ({(int)response.StatusCode}).");
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

        var rootUrl = baseUrl!.TrimEnd('/');
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{rootUrl}/openai/models?api-version=2024-02-01");
        request.Headers.Add("api-key", apiKey.Trim());

        using var response = await http.SendAsync(request, cancellationToken);
        stopwatch.Stop();

        return InterpretAuthStatus(response, stopwatch.Elapsed,
            successMessage: $"Authenticated to Azure OpenAI ({(int)response.StatusCode}).");
    }

    private static PlatformAiProbeResult InterpretAuthStatus(
        HttpResponseMessage response,
        TimeSpan elapsed,
        string successMessage)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return new PlatformAiProbeResult(true, status, successMessage, elapsed);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                new PlatformAiProbeResult(false, status, "API key rejected (401 Unauthorized).", elapsed),
            HttpStatusCode.Forbidden =>
                new PlatformAiProbeResult(false, status, "API key lacks permission (403 Forbidden).", elapsed),
            HttpStatusCode.NotFound =>
                new PlatformAiProbeResult(false, status,
                    "Endpoint or model not found (404). Check base URL and model name.", elapsed),
            HttpStatusCode.TooManyRequests =>
                new PlatformAiProbeResult(false, status,
                    "Rate-limited (429). Key looks valid, retry shortly.", elapsed),
            _ =>
                new PlatformAiProbeResult(false, status,
                    $"Provider returned {status} {response.ReasonPhrase}.", elapsed),
        };
    }
}
