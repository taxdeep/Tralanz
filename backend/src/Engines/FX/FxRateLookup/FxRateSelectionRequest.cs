namespace Engines.FX.FxRateLookup;

public sealed record class FxRateSelectionRequest(
    Guid CompanyId,
    Guid? CreatedByUserId,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    DateOnly RequestedDate,
    string ProviderKey,
    int LookbackDays,
    int Take = 6);
