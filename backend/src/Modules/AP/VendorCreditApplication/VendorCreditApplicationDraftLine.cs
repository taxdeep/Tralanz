namespace Modules.AP.VendorCreditApplication;

public sealed record VendorCreditApplicationDraftLine(
    Guid SourceVendorCreditOpenItemId,
    Guid TargetBillOpenItemId,
    decimal AppliedAmountTx);
