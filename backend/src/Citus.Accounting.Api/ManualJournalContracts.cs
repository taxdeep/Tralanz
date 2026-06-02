namespace Citus.Accounting.Api;

public sealed record PrepareFxRevaluationBatchHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? BookId,
    DateOnly RevaluationDate,
    string TransactionCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    bool IncludeAccountsReceivable,
    bool IncludeAccountsPayable,
    string? Memo);

public sealed record PrepareFxRevaluationUnwindBatchHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    DateOnly UnwindDate,
    string? Memo,
    string? IdempotencyKey = null);

public sealed record FxRevaluationCascadeUnwindPlanQuery(CompanyId CompanyId);

public sealed record CompanyBookGovernanceLookupQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate);

public sealed record CompanyBookGovernedChangePreviewHttpRequest(
    CompanyId CompanyId,
    Guid? BookId,
    DateOnly? AsOfDate,
    bool? IsPrimary,
    string? AccountingStandard,
    string? BookBaseCurrencyCode,
    string? FunctionalCurrencyCode,
    string? PresentationCurrencyCode,
    string? RateType,
    string? QuoteBasis,
    string? RateUseCase,
    string? PostingReason,
    string? RevaluationProfile,
    string? FxRoundingPolicy);

public sealed record PrepareCompanyBookGovernedChangeRequestHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? BookId,
    DateOnly? AsOfDate,
    DateOnly EffectiveFrom,
    bool? IsPrimary,
    string? AccountingStandard,
    string? BookBaseCurrencyCode,
    string? FunctionalCurrencyCode,
    string? PresentationCurrencyCode,
    string? RateType,
    string? QuoteBasis,
    string? RateUseCase,
    string? PostingReason,
    string? RevaluationProfile,
    string? FxRoundingPolicy);

public sealed record CompanyBookGovernedChangeRequestLookupQuery(CompanyId CompanyId);

public sealed record TransitionCompanyBookGovernedChangeRequestHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record CompanyBookGovernedChangeRequestReadinessQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate);

public sealed record CompanyBookGovernanceSignalsLookupQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate);

public sealed record CreateCompanyBookGovernanceSignalHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    string SignalType,
    DateOnly SignalDate,
    string? ReferenceLabel,
    string? Notes);

public sealed record RegisterCompanyBookClosedPeriodHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    DateOnly PeriodEndDate,
    string? ReferenceLabel,
    string? Notes);

public sealed record RegisterCompanyBookIssuedStatementHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    DateOnly IssuedOn,
    string StatementLabel,
    string? Notes);

public sealed record RegisterCompanyBookFiledTaxHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    DateOnly FiledOn,
    string FilingLabel,
    string? Notes);

public sealed record CompanyBookPolicyLookupQuery(
    CompanyId CompanyId,
    Guid? BookId,
    DateOnly? AsOfDate);

public sealed record PostManualJournalHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record ManualJournalLookupQuery(CompanyId CompanyId);

public sealed record DocumentReviewLookupQuery(CompanyId CompanyId);

/// <summary>
/// HTTP body for POST /document-review/invoice/{id}/send. ToEmail is
/// required and must contain '@'; everything else is optional. The
/// composer fills sensible defaults from the invoice / customer when
/// the operator leaves Cc / Bcc / Message blank.
/// </summary>
public sealed record InvoiceSendHttpRequest(
    string ToEmail,
    string? Cc,
    string? Bcc,
    string? Message);

/// <summary>
/// HTTP body for POST / PUT /invoice-templates. All branding fields are
/// optional in the wire shape — null / missing keys collapse to the
/// canonical InvoiceTemplateConfig.Default. The endpoint validates
/// non-null hex colors and required textual fields.
/// </summary>
public sealed record InvoiceTemplateUpsertHttpRequest(
    string Name,
    string? LogoUrl,
    string? PrimaryColorHex,
    string? AccentColorHex,
    string? Tagline,
    string? Greeting,
    string? PaymentInstructions,
    string? FooterNote,
    bool? ShowTaxColumn,
    string? EmailSubjectTemplate,
    string? EmailBodyTemplate);

public sealed record DocumentLifecycleRequestReadinessQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate);

public sealed record OpenItemDrillDownLookupQuery(CompanyId CompanyId);

public sealed record OpenItemAdjustmentPreviewLookupQuery(
    CompanyId CompanyId,
    string? AdjustmentType,
    DateOnly? AdjustmentDate,
    decimal? AdjustmentAmountTx);

public sealed record RequestOpenItemAdjustmentHttpRequest(
    CompanyId CompanyId,
    UserId? UserId,
    string AdjustmentType,
    DateOnly? AdjustmentDate,
    decimal? AdjustmentAmountTx,
    string? Reason);

public sealed record TransitionOpenItemAdjustmentRequestHttpRequest(
    CompanyId CompanyId,
    UserId? UserId);

public sealed record GovernOpenItemAdjustmentApprovalHttpRequest(
    CompanyId CompanyId,
    UserId? UserId);

public sealed record OpenItemAdjustmentRequestReadinessQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate);

public sealed record ExecuteOpenItemAdjustmentRequestHttpRequest(
    CompanyId CompanyId,
    UserId? UserId,
    Guid AdjustmentAccountId,
    DateOnly? AsOfDate,
    string? IdempotencyKey);

public sealed record OpenItemAdjustmentAccountMappingLookupQuery(
    CompanyId CompanyId,
    string? OpenItemType,
    string? AdjustmentType,
    bool? IncludeInactive,
    Guid? BookId,
    string? PolicyScope,
    string? SearchText,
    int? Limit);

public sealed record SaveOpenItemAdjustmentAccountMappingHttpRequest(
    CompanyId CompanyId,
    UserId? UserId,
    Guid? BookId,
    string OpenItemType,
    string AdjustmentType,
    Guid AdjustmentAccountId);

public sealed record DeactivateOpenItemAdjustmentAccountMappingHttpRequest(
    CompanyId CompanyId,
    UserId? UserId);

public sealed record SourceDocumentBrowserLookupQuery(
    CompanyId CompanyId,
    string? SourceType,
    string? CounterpartyRole,
    Guid? CounterpartyId,
    int? Limit);

public sealed record PostInvoiceHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveInvoiceDraftHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    Guid? FxSnapshotId,
    decimal? FxRate,
    DateOnly? FxEffectiveDate,
    string? FxSource,
    string? Memo,
    IReadOnlyList<SaveInvoiceDraftLineHttpRequest> Lines,
    string? CustomerPoNumber = null,
    Guid? SalesOrderId = null,
    // Optimistic-concurrency token. Round-trips the updated_at the
    // editor saw on GET; the repository rejects the UPDATE with a 409
    // if the value no longer matches the row's current updated_at.
    // Null on first save / when the editor opts out of the check.
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record SaveInvoiceDraftLineHttpRequest(
    int LineNumber,
    Guid RevenueAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    decimal TaxAmount,
    Guid? ItemId = null,
    Guid? WarehouseId = null,
    string? UomCode = null,
    // Per-line back-link to the Task this line is billing. When non-null
    // the line persists into invoice_lines.task_id (column added by the
    // Batch 8 PostgresTaskLinkSchemaInitializer), and the post handler
    // uses these distinct task_ids to flip the source tasks
    // Completed -> Billed after the invoice posts.
    Guid? TaskId = null,
    // H6-2: optional pin to a specific task_lines row. When present,
    // the post handler stamps THAT line as billed and recomputes the
    // task header status (Open|Completed -> PartiallyBilled, or ->
    // Billed when this is the final un-billed line). Null falls back
    // to the legacy whole-task path via TaskId alone (matches
    // pre-H6-2 behavior; existing drafts in the DB without
    // task_line_id keep working unchanged).
    Guid? TaskLineId = null,
    // R2: tax_code_sets.id — a Tax Code bundle selected on this line.
    Guid? TaxCodeSetId = null);

public sealed record InvoiceLookupQuery(CompanyId CompanyId);

public sealed record PostCreditNoteHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveCreditNoteDraftHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    DateOnly CreditNoteDate,
    DateOnly DueDate,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    Guid? FxSnapshotId,
    decimal? FxRate,
    DateOnly? FxEffectiveDate,
    string? FxSource,
    string? Memo,
    IReadOnlyList<SaveCreditNoteDraftLineHttpRequest> Lines);

public sealed record SaveCreditNoteDraftLineHttpRequest(
    int LineNumber,
    Guid RevenueAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    decimal TaxAmount,
    // Optional Task back-link. When non-null and the credit note is
    // posted, the Task billing coordinator rolls every linked task
    // currently in Billed status back to Completed. Persists into
    // credit_note_lines.task_id (column added by Batch 8).
    Guid? TaskId = null,
    // H6-3: optional pin to a specific task_lines row that this
    // credit releases. When present, the post handler routes through
    // the new RollbackLinesAsync path (per-line audit). Null falls
    // back to legacy whole-task rollback via TaskId.
    Guid? TaskLineId = null);

public sealed record CreditNoteLookupQuery(CompanyId CompanyId);

public sealed record PostBillHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveBillDraftHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    DateOnly BillDate,
    DateOnly DueDate,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    Guid? FxSnapshotId,
    decimal? FxRate,
    DateOnly? FxEffectiveDate,
    string? FxSource,
    string? Memo,
    IReadOnlyList<SaveBillDraftLineHttpRequest> Lines,
    Guid? PaymentTermId = null,
    Guid? SourcePurchaseOrderId = null,
    string? SourcePurchaseOrderNumber = null,
    string? BillNumber = null);

public sealed record SaveBillDraftLineHttpRequest(
    int LineNumber,
    Guid ExpenseAccountId,
    string Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal TaxAmount,
    bool IsTaxRecoverable,
    Guid? ItemId,
    Guid? WarehouseId,
    string? UomCode,
    decimal? Quantity,
    decimal? UnitCost,
    Guid? PurchaseOrderId = null,
    int? PurchaseOrderLineNumber = null,
    Guid? TaxCodeSetId = null,
    Guid? TaskId = null);

public sealed record BillLookupQuery(CompanyId CompanyId);

public sealed record SubmitBillDraftHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record SaveReceiptDraftHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    Guid WarehouseId,
    DateOnly ReceiptDate,
    string? VendorReference,
    string? SourceReference,
    string? Memo,
    IReadOnlyList<SaveReceiptDraftLineHttpRequest> Lines);

public sealed record SaveReceiptDraftLineHttpRequest(
    int LineNumber,
    Guid ItemId,
    decimal Quantity,
    string UomCode,
    string? TrackingCaptureHome,
    Guid? PurchaseOrderId = null,
    int? PurchaseOrderLineNumber = null);

public sealed record ReceiptLookupQuery(CompanyId CompanyId);

public sealed record ReceiptListQuery(CompanyId CompanyId, int? Take);

public sealed record SavePurchaseOrderDraftHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    string? VendorReference,
    string? Memo,
    IReadOnlyList<SavePurchaseOrderDraftLineHttpRequest> Lines);

public sealed record SavePurchaseOrderDraftLineHttpRequest(
    int LineNumber,
    Guid ItemId,
    decimal OrderedQuantity,
    string UomCode,
    string? Description,
    decimal? UnitCost);

public sealed record PurchaseOrderLookupQuery(CompanyId CompanyId);

public sealed record PurchaseOrderLifecycleAuditQuery(CompanyId CompanyId, int? Take);

public sealed record PurchaseOrderListQuery(CompanyId CompanyId, int? Take);

public sealed record PurchaseOrderApprovalRequestListQuery(CompanyId CompanyId, int? Take, bool? IncludeClosed);

public sealed record RequestPurchaseOrderApprovalHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    string? Reason);

public sealed record SubmitPurchaseOrderApprovalRequestHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record RejectPurchaseOrderApprovalRequestHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record ReversePurchaseOrderApprovalHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record ApprovePurchaseOrderHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record IssuePurchaseOrderHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record ReopenPurchaseOrderForAmendmentHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record ClosePurchaseOrderHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record CancelPurchaseOrderHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record RefreshPurchaseOrderQuantityDiscrepanciesHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record ReviewPurchaseOrderQuantityDiscrepancyHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    int PurchaseOrderLineNumber,
    string DiscrepancyType,
    string InvestigationStatus,
    string? ReviewNote);

public sealed record PostReceiptDraftHttpRequest(
    CompanyId CompanyId,
    UserId UserId);

public sealed record PostReceiptGrIrBridgeHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? GrIrClearingAccountId,
    string? IdempotencyKey);

public sealed record ExecuteReceiptGrIrSettlementHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    decimal? SettlementAmountBase,
    string? IdempotencyKey);

public sealed record PostReceiptGrIrSettlementJournalHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    string? IdempotencyKey);

public sealed record SaveReceiptGrIrClearingAccountPolicyHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid GrIrClearingAccountId);

public sealed record PostVendorCreditHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveVendorCreditDraftHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    DateOnly VendorCreditDate,
    DateOnly DueDate,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    Guid? FxSnapshotId,
    decimal? FxRate,
    DateOnly? FxEffectiveDate,
    string? FxSource,
    string? Memo,
    IReadOnlyList<SaveVendorCreditDraftLineHttpRequest> Lines);

public sealed record SaveVendorCreditDraftLineHttpRequest(
    int LineNumber,
    Guid ExpenseAccountId,
    string Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal TaxAmount,
    bool IsTaxRecoverable);

public sealed record VendorCreditLookupQuery(CompanyId CompanyId);

public sealed record PrepareSettlementDraftLineHttpRequest(
    Guid TargetOpenItemId,
    decimal AppliedAmountTx);

public sealed record PrepareReceivePaymentDraftHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    Guid? AcceptedFxSnapshotId,
    string? Memo,
    IReadOnlyList<PrepareSettlementDraftLineHttpRequest> Lines,
    /// <summary>Overpayment slice the form parked as a Customer Deposit.
    /// Defaults to 0 — keeps existing single-payment-per-invoice flows
    /// behaving exactly as before. The repo creates the deposit when > 0.</summary>
    decimal ExtraDepositAmount = 0m);

public sealed record OpenReceivablesLookupQuery(CompanyId CompanyId);

public sealed record PostReceivePaymentHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record ReceivePaymentLookupQuery(CompanyId CompanyId);

public sealed record PostCreditApplicationHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    string? IdempotencyKey);

public sealed record CreditApplicationLookupQuery(CompanyId CompanyId);

public sealed record PostPayBillHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record PreparePayBillDraftHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    Guid? AcceptedFxSnapshotId,
    string? Memo,
    IReadOnlyList<PrepareSettlementDraftLineHttpRequest> Lines);

public sealed record OpenPayablesLookupQuery(CompanyId CompanyId);

public sealed record PayBillLookupQuery(CompanyId CompanyId);

public sealed record PostVendorCreditApplicationHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    string? IdempotencyKey);

public sealed record VendorCreditApplicationLookupQuery(CompanyId CompanyId);

public sealed record PostFxRevaluationBatchHttpRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record FxRevaluationBatchListQuery(CompanyId CompanyId, int? Take);

public sealed record FxRevaluationBatchLookupQuery(CompanyId CompanyId);

/// <summary>
/// Shared lookup query for the V1-pending detail endpoints (sales-
/// receipts, refund-receipts, bank-transfers, bank-deposits,
/// tax-returns). Single-field record so [AsParameters] picks the
/// CompanyId off the query string the same way the existing
/// detail-endpoint queries do.
/// </summary>
public sealed record V1PendingLookupQuery(CompanyId CompanyId);
