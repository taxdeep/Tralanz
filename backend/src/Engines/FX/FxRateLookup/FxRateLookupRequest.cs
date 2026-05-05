namespace Engines.FX.FxRateLookup;

public sealed record class FxRateLookupRequest(
    CompanyId CompanyId,
    UserId? CreatedByUserId,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    DateOnly RequestedDate,
    string ProviderKey,
    int LookbackDays,
    string RateType,
    string QuoteBasis,
    string RateUseCase,
    string PostingReason);
