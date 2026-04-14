namespace Citus.Accounting.Api;

public sealed record PrepareFxRevaluationBatchHttpRequest(
    Guid CompanyId,
    Guid UserId,
    DateOnly RevaluationDate,
    string TransactionCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    bool IncludeAccountsReceivable,
    bool IncludeAccountsPayable,
    string? Memo);

public sealed record PrepareFxRevaluationUnwindBatchHttpRequest(
    Guid CompanyId,
    Guid UserId,
    DateOnly UnwindDate,
    string? Memo,
    string? IdempotencyKey = null);

public sealed record FxRevaluationCascadeUnwindPlanQuery(Guid CompanyId);

public sealed record PostManualJournalHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record ManualJournalLookupQuery(Guid CompanyId);

public sealed record DocumentReviewLookupQuery(Guid CompanyId);

public sealed record PostInvoiceHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record InvoiceLookupQuery(Guid CompanyId);

public sealed record PostCreditNoteHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record CreditNoteLookupQuery(Guid CompanyId);

public sealed record PostBillHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record BillLookupQuery(Guid CompanyId);

public sealed record PostVendorCreditHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record VendorCreditLookupQuery(Guid CompanyId);

public sealed record PrepareSettlementDraftLineHttpRequest(
    Guid TargetOpenItemId,
    decimal AppliedAmountTx);

public sealed record PrepareReceivePaymentDraftHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid CustomerId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    Guid? AcceptedFxSnapshotId,
    string? Memo,
    IReadOnlyList<PrepareSettlementDraftLineHttpRequest> Lines);

public sealed record OpenReceivablesLookupQuery(Guid CompanyId);

public sealed record PostReceivePaymentHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record ReceivePaymentLookupQuery(Guid CompanyId);

public sealed record PostCreditApplicationHttpRequest(
    Guid CompanyId,
    Guid UserId,
    string? IdempotencyKey);

public sealed record CreditApplicationLookupQuery(Guid CompanyId);

public sealed record PostPayBillHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record PreparePayBillDraftHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid VendorId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    Guid? AcceptedFxSnapshotId,
    string? Memo,
    IReadOnlyList<PrepareSettlementDraftLineHttpRequest> Lines);

public sealed record OpenPayablesLookupQuery(Guid CompanyId);

public sealed record PayBillLookupQuery(Guid CompanyId);

public sealed record PostVendorCreditApplicationHttpRequest(
    Guid CompanyId,
    Guid UserId,
    string? IdempotencyKey);

public sealed record VendorCreditApplicationLookupQuery(Guid CompanyId);

public sealed record PostFxRevaluationBatchHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record FxRevaluationBatchLookupQuery(Guid CompanyId);
