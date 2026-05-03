using Citus.Accounting.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Citus.Accounting.Infrastructure.Fx;

/// <summary>
/// Recommends a per-document FX rate by consulting the
/// <c>fx_rates_daily</c> cache first, then falling through to a live
/// frankfurter call, and finally to a "most recent business close
/// within N days" cache lookup if frankfurter is unreachable.
///
/// Recommendation rule: for a document dated D, the recommended rate is
/// the rate published on D-1 (previous calendar day). Frankfurter
/// silently returns the most recent prior business-day rate when the
/// requested date is a weekend / holiday — we honour that and store the
/// rate under whatever date frankfurter says it was published, so the
/// cache key is always "the day this rate actually closed", not "the
/// day someone asked for it".
///
/// <para>
/// Direction convention: callers (Invoice / Bill / JE) expect the
/// returned rate in the system's <c>tx_to_base</c> form — i.e.
/// <c>baseAmount = txAmount * rate</c>, where the document's transaction
/// currency is the <c>QuoteCurrencyCode</c> and the company base is the
/// <c>BaseCurrencyCode</c>. Frankfurter publishes the opposite direction
/// (<c>1 base = X quote</c>); we invert at the boundary so consumers,
/// the cache, and the posting engine all speak the same language.
/// Pre-fix cache rows are tagged <c>value_basis='frankfurter'</c> and
/// filtered out by the cache repo's reads.
/// </para>
/// </summary>
public sealed class LocalFirstRecommendedFxRateService : IRecommendedFxRateService
{
    /// <summary>
    /// Maximum days we'll look back when frankfurter is unreachable and
    /// we have to recommend something based on stale cache. ECB has at
    /// most 4 consecutive non-business days (long weekend + holiday) in
    /// the calendar, but giving the cache a 14-day window covers
    /// extended outages where frankfurter itself is down.
    /// </summary>
    private const int FallbackLookbackDays = 14;

    private readonly IFxRateCacheRepository _cache;
    private readonly IFrankfurterFxRateClient _frankfurter;
    private readonly ILogger<LocalFirstRecommendedFxRateService> _logger;

    public LocalFirstRecommendedFxRateService(
        IFxRateCacheRepository cache,
        IFrankfurterFxRateClient frankfurter,
        ILogger<LocalFirstRecommendedFxRateService> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _frankfurter = frankfurter ?? throw new ArgumentNullException(nameof(frankfurter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RecommendedFxRate?> GetAsync(
        DateOnly documentDate,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseCurrencyCode) || string.IsNullOrWhiteSpace(quoteCurrencyCode))
        {
            return null;
        }

        var baseCode = baseCurrencyCode.Trim().ToUpperInvariant();
        var quoteCode = quoteCurrencyCode.Trim().ToUpperInvariant();

        if (string.Equals(baseCode, quoteCode, StringComparison.Ordinal))
        {
            return new RecommendedFxRate(
                documentDate,
                baseCode,
                quoteCode,
                1m,
                "identity",
                IsStale: false);
        }

        var targetDate = documentDate.AddDays(-1);

        // 1. Cache hit on D-1 — fastest path, no network.
        var cached = await _cache.GetAsync(targetDate, baseCode, quoteCode, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return ToRecommended(cached, isStale: false);
        }

        // 2. Live frankfurter fetch. Stores every quote in the response
        // (not just the one the caller asked for) so a follow-up lookup
        // for a different quote currency on the same day is a cache hit.
        // Each rate is inverted at this boundary so cache + caller all
        // see the system's tx_to_base direction (see class doc-comment).
        var live = await _frankfurter.GetRatesAsync(targetDate, baseCode, cancellationToken).ConfigureAwait(false);
        if (live is not null)
        {
            var rows = live.Rates
                .Where(kv => kv.Value > 0m)
                .Select(kv => new FxRateCacheRow(
                    live.RateDate,
                    live.BaseCurrencyCode,
                    kv.Key.ToUpperInvariant(),
                    Math.Round(1m / kv.Value, 10, MidpointRounding.ToEven),
                    FrankfurterFxRateClient.ProviderKey))
                .ToArray();

            try
            {
                await _cache.UpsertManyAsync(rows, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist frankfurter rates to fx_rates_daily; serving in-memory.");
            }

            var match = rows.FirstOrDefault(r => string.Equals(r.QuoteCurrencyCode, quoteCode, StringComparison.Ordinal));
            if (match is not null)
            {
                // Frankfurter returns the most recent prior business-day
                // rate when the requested date is a non-business day, so
                // the row's RateDate may be earlier than targetDate. Mark
                // stale only if the response date is older than what we
                // asked for AND the gap is more than the calendar day
                // we'd normally accept (D-1 → D-1 = fresh; D-1 → D-3 =
                // weekend rollback, still fresh; D-1 → D-7 = treat as
                // stale).
                var isStale = (targetDate.DayNumber - match.RateDate.DayNumber) > 4;
                return ToRecommended(match, isStale);
            }
        }

        // 3. Frankfurter unreachable / no match → fall back to most
        // recent cached row in the lookback window. Mark stale so the UI
        // can show a hint.
        var stale = await _cache.GetLatestBeforeAsync(
            targetDate,
            baseCode,
            quoteCode,
            FallbackLookbackDays,
            cancellationToken).ConfigureAwait(false);

        return stale is not null ? ToRecommended(stale, isStale: true) : null;
    }

    private static RecommendedFxRate ToRecommended(FxRateCacheRow row, bool isStale) =>
        new(
            row.RateDate,
            row.BaseCurrencyCode,
            row.QuoteCurrencyCode,
            row.Rate,
            row.Source,
            isStale);
}
