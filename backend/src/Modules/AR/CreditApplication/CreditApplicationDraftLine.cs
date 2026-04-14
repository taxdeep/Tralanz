namespace Modules.AR.CreditApplication;

public sealed record CreditApplicationDraftLine(
    Guid SourceCreditOpenItemId,
    Guid TargetInvoiceOpenItemId,
    decimal AppliedAmountTx);
