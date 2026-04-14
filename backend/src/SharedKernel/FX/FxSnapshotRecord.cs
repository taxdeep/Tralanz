namespace SharedKernel.FX;

public sealed record class FxSnapshotRecord(
    Guid Id,
    Guid CompanyId,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    DateOnly RequestedDate,
    DateOnly EffectiveDate,
    decimal Rate,
    string? ProviderKey,
    string RowOrigin,
    string SnapshotSemantics,
    Guid? SystemMarketRateId,
    DateTimeOffset CreatedAt);
