namespace Modules.AP.VendorCreditApplication;

public sealed record VendorCreditApplicationOpenItemCandidate(
    Guid OpenItemId,
    Guid VendorId,
    string SourceType,
    Guid SourceId,
    string DisplayNumber,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    decimal OriginalAmountTx,
    decimal OpenAmountTx,
    decimal OpenAmountBase,
    string BalanceSide,
    string Status);
