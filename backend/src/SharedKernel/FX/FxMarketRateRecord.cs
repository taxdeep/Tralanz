namespace SharedKernel.FX;

public sealed record class FxMarketRateRecord(
    Guid Id,
    string ProviderKey,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    DateOnly MarketDate,
    decimal Rate,
    string RateType,
    string QuoteBasis,
    DateTimeOffset FetchedAt,
    string? PayloadJson);
