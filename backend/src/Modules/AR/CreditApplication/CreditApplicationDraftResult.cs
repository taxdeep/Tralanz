namespace Modules.AR.CreditApplication;

public sealed record CreditApplicationDraftResult(
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
