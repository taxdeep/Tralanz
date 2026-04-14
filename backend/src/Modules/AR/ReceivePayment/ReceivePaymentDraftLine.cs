namespace Modules.AR.ReceivePayment;

public sealed record ReceivePaymentDraftLine(
    Guid TargetOpenItemId,
    decimal AppliedAmountTx);
