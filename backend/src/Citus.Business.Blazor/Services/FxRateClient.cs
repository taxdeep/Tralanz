using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the recommended-FX-rate endpoint. Returns null on
/// any non-success (404, network error, server error) — the caller
/// surfaces the gap to the user as an empty input + "manual entry
/// required" hint, so the UI must never block on this.
/// </summary>
public sealed class FxRateClient(HttpClient httpClient, ILogger<FxRateClient> logger)
{
    public async Task<RecommendedFxRateSummary?> GetRecommendedAsync(
        DateOnly date,
        string baseCode,
        string quoteCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseCode) || string.IsNullOrWhiteSpace(quoteCode))
        {
            return null;
        }

        var url = $"accounting/fx-rates/recommended?date={date:yyyy-MM-dd}&baseCode={Uri.EscapeDataString(baseCode)}&quoteCode={Uri.EscapeDataString(quoteCode)}";

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadFromJsonAsync<RecommendedFxRateSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to fetch recommended FX rate {Base}->{Quote} on {Date}.", baseCode, quoteCode, date);
            return null;
        }
    }
}

public sealed record RecommendedFxRateSummary(
    DateOnly RateDate,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    decimal Rate,
    string Source,
    bool IsStale);
