namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Returns a recommended FX rate for a document with a given posting
/// date, looking up D-1 (previous calendar day) of the document date.
/// The recommended rate is what the UI pre-fills into a document's
/// fx_rate field — but the user is always free to override, and the
/// override is what the posting engine ultimately writes into the
/// per-transaction snapshot. This service is strictly a *recommendation*
/// source; it does not affect already-posted documents.
///
/// Lookup strategy is "DB cache → frankfurter live fetch → DB cache
/// (most recent business-day fallback)". The DB cache is shared across
/// all companies because FX rates are global market data — one row per
/// (rate_date, base_code, quote_code) regardless of who triggered the
/// fetch.
/// </summary>
public interface IRecommendedFxRateService
{
    Task<RecommendedFxRate?> GetAsync(
        DateOnly documentDate,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        CancellationToken cancellationToken);
}

public sealed record RecommendedFxRate(
    DateOnly RateDate,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    decimal Rate,
    string Source,
    bool IsStale);

/// <summary>
/// Read/write surface for the global <c>fx_rates_daily</c> cache.
/// Rows are global (no company_id) because frankfurter publishes ECB
/// market rates that are identical for every Citus tenant.
/// </summary>
public interface IFxRateCacheRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<FxRateCacheRow?> GetAsync(
        DateOnly rateDate,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent cache row strictly before
    /// <paramref name="upperBoundDate"/> within the lookback window.
    /// Used as a fallback when frankfurter is unreachable and we need
    /// some plausible recent close to recommend.
    /// </summary>
    Task<FxRateCacheRow?> GetLatestBeforeAsync(
        DateOnly upperBoundDate,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        int maxLookbackDays,
        CancellationToken cancellationToken);

    Task UpsertManyAsync(
        IReadOnlyList<FxRateCacheRow> rows,
        CancellationToken cancellationToken);
}

public sealed record FxRateCacheRow(
    DateOnly RateDate,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    decimal Rate,
    string Source);

/// <summary>
/// Thin adapter over <c>frankfurter.dev</c>. Returns the published rates
/// for a given date with a chosen base currency, or null if the API
/// can't be reached / returns an error. frankfurter's behaviour for
/// non-business days: it silently returns the most recent prior
/// business-day close, so the response's <see cref="FrankfurterRatesResponse.RateDate"/>
/// may be earlier than the requested date — callers store the response
/// under <c>RateDate</c>, not the request date, so the cache reflects
/// what frankfurter actually published.
/// </summary>
public interface IFrankfurterFxRateClient
{
    Task<FrankfurterRatesResponse?> GetRatesAsync(
        DateOnly requestedDate,
        string baseCurrencyCode,
        CancellationToken cancellationToken);
}

public sealed record FrankfurterRatesResponse(
    DateOnly RateDate,
    string BaseCurrencyCode,
    IReadOnlyDictionary<string, decimal> Rates);
