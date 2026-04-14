namespace SharedKernel.FX;

public sealed record class FxMarketRateRecord(
    Guid Id,
    string ProviderKey,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    DateOnly MarketDate,
    decimal Rate,
    DateTimeOffset FetchedAt,
    string? PayloadJson);
