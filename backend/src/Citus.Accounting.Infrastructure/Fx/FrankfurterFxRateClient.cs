using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Citus.Accounting.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Citus.Accounting.Infrastructure.Fx;

/// <summary>
/// Live adapter over <c>frankfurter.dev</c>. Calls
/// <c>GET https://api.frankfurter.dev/v1/{date}?base={base}</c> and
/// returns every quote in one go (the API doesn't support filtering to a
/// single quote currency, but it's a tiny payload).
///
/// Behaviour worth knowing:
///   * Non-business days (weekends, ECB holidays) return the most recent
///     prior business-day rates with the response's <c>date</c> field set
///     to that earlier date — we surface that earlier date so the cache
///     stores the rate under the day it was actually published.
///   * Unknown base currency → 404. Unreachable network → exception.
///     Both are caught here and turned into <c>null</c>; the orchestrating
///     <see cref="LocalFirstRecommendedFxRateService"/> handles the
///     fallback logic.
/// </summary>
public sealed class FrankfurterFxRateClient : IFrankfurterFxRateClient
{
    public const string ProviderKey = "frankfurter";
    public const string ProviderBaseUrl = "https://api.frankfurter.dev";

    private readonly HttpClient _httpClient;
    private readonly ILogger<FrankfurterFxRateClient> _logger;

    public FrankfurterFxRateClient(HttpClient httpClient, ILogger<FrankfurterFxRateClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<FrankfurterRatesResponse?> GetRatesAsync(
        DateOnly requestedDate,
        string baseCurrencyCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return null;
        }

        var path = $"v1/{requestedDate:yyyy-MM-dd}?base={Uri.EscapeDataString(baseCurrencyCode.Trim().ToUpperInvariant())}";

        try
        {
            using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "frankfurter returned {StatusCode} for date={Date} base={Base}.",
                    (int)response.StatusCode,
                    requestedDate,
                    baseCurrencyCode);
                return null;
            }

            var raw = await response.Content.ReadFromJsonAsync<FrankfurterRawResponse>(cancellationToken).ConfigureAwait(false);
            if (raw is null || string.IsNullOrEmpty(raw.Base) || raw.Rates is null || raw.Rates.Count == 0)
            {
                return null;
            }

            if (!DateOnly.TryParseExact(raw.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rateDate))
            {
                _logger.LogWarning("frankfurter response date '{Date}' did not parse as ISO yyyy-MM-dd.", raw.Date);
                return null;
            }

            return new FrankfurterRatesResponse(
                rateDate,
                raw.Base.ToUpperInvariant(),
                raw.Rates);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "frankfurter request failed (network) for date={Date} base={Base}.", requestedDate, baseCurrencyCode);
            return null;
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "frankfurter request timed out for date={Date} base={Base}.", requestedDate, baseCurrencyCode);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "frankfurter response did not deserialize for date={Date} base={Base}.", requestedDate, baseCurrencyCode);
            return null;
        }
    }

    private sealed record FrankfurterRawResponse(
        decimal Amount,
        string Base,
        string Date,
        Dictionary<string, decimal> Rates);
}
