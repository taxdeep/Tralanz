namespace Modules.AR.ReceivePayment;

public sealed record ReceivePaymentOpenItemCandidate(
    Guid OpenItemId,
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
