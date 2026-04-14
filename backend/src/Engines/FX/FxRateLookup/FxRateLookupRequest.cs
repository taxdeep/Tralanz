namespace Engines.FX.FxRateLookup;

public sealed record class FxRateLookupRequest(
    Guid CompanyId,
    Guid? CreatedByUserId,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    DateOnly RequestedDate,
    string ProviderKey,
    int LookbackDays,
    string RateType,
    string QuoteBasis,
    string RateUseCase,
    string PostingReason);
