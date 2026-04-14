namespace Engines.FX.FxRateLookup;

public sealed record class FxRateSelectionRequest(
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
    string PostingReason,
    int Take = 6);
