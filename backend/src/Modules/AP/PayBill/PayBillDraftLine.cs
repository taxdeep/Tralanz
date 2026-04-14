namespace Modules.AP.PayBill;

public sealed record PayBillDraftLine(
    Guid TargetOpenItemId,
    decimal AppliedAmountTx);
