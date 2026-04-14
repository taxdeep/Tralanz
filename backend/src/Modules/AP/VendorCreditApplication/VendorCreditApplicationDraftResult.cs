namespace Modules.AP.VendorCreditApplication;

public sealed record VendorCreditApplicationDraftResult(
    Guid DocumentId,
    string EntityNumber,
    string ApplicationNumber,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    decimal TotalAmount,
    decimal RealizedFxAmountBase,
    int LineCount,
    string Status)
{
    public bool HasRealizedFxDifference => RealizedFxAmountBase != 0m;
}
