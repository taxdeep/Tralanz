namespace Citus.Accounting.Api;

public sealed record PrepareFxRevaluationBatchHttpRequest(
    CompanyId CompanyId,
    Guid? BookId,
    DateOnly RevaluationDate,
    string TransactionCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    bool IncludeAccountsReceivable,
    bool IncludeAccountsPayable,
    string? Memo);

public sealed record PrepareFxRevaluationUnwindBatchHttpRequest(
    CompanyId CompanyId,
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
    CompanyId CompanyId);

public sealed record CompanyBookGovernedChangeRequestReadinessQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate);

public sealed record CompanyBookGovernanceSignalsLookupQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate);

public sealed record CreateCompanyBookGovernanceSignalHttpRequest(
    CompanyId CompanyId,
    string SignalType,
    DateOnly SignalDate,
    string? ReferenceLabel,
    string? Notes);

public sealed record RegisterCompanyBookClosedPeriodHttpRequest(
    CompanyId CompanyId,
    DateOnly PeriodEndDate,
    string? ReferenceLabel,
    string? Notes);

public sealed record RegisterCompanyBookIssuedStatementHttpRequest(
    CompanyId CompanyId,
    DateOnly IssuedOn,
    string StatementLabel,
    string? Notes);

public sealed record RegisterCompanyBookFiledTaxHttpRequest(
    CompanyId CompanyId,
    DateOnly FiledOn,
    string FilingLabel,
    string? Notes);

public sealed record CompanyBookPolicyLookupQuery(
    CompanyId CompanyId,
    Guid? BookId,
    DateOnly? AsOfDate);

public sealed record PostManualJournalHttpRequest(
    CompanyId CompanyId,
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
    string AdjustmentType,
    DateOnly? AdjustmentDate,
    decimal? AdjustmentAmountTx,
    string? Reason);

public sealed record TransitionOpenItemAdjustmentRequestHttpRequest(
    CompanyId CompanyId);

public sealed record GovernOpenItemAdjustmentApprovalHttpRequest(
    CompanyId CompanyId);

public sealed record OpenItemAdjustmentRequestReadinessQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate);

public sealed record ExecuteOpenItemAdjustmentRequestHttpRequest(
    CompanyId CompanyId,
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
    Guid? BookId,
    string OpenItemType,
    string AdjustmentType,
    Guid AdjustmentAccountId);

public sealed record DeactivateOpenItemAdjustmentAccountMappingHttpRequest(
    CompanyId CompanyId);

public sealed record SourceDocumentBrowserLookupQuery(
    CompanyId CompanyId,
    string? SourceType,
    string? CounterpartyRole,
    Guid? CounterpartyId,
    int? Limit);

public sealed record PostInvoiceHttpRequest(
    CompanyId CompanyId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveInvoiceDraftHttpRequest(
    CompanyId CompanyId,
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
    string? UomCode = null);

public sealed record InvoiceLookupQuery(CompanyId CompanyId);

public sealed record PostCreditNoteHttpRequest(
    CompanyId CompanyId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveCreditNoteDraftHttpRequest(
    CompanyId CompanyId,
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
    decimal TaxAmount);

public sealed record CreditNoteLookupQuery(CompanyId CompanyId);

public sealed record PostBillHttpRequest(
    CompanyId CompanyId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveBillDraftHttpRequest(
    CompanyId CompanyId,
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
    IReadOnlyList<SaveBillDraftLineHttpRequest> Lines);

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
    int? PurchaseOrderLineNumber = null);

public sealed record BillLookupQuery(CompanyId CompanyId);

public sealed record SubmitBillDraftHttpRequest(
    CompanyId CompanyId);

public sealed record SaveReceiptDraftHttpRequest(
    CompanyId CompanyId,
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
    string? Reason);

public sealed record SubmitPurchaseOrderApprovalRequestHttpRequest(
    CompanyId CompanyId);

public sealed record RejectPurchaseOrderApprovalRequestHttpRequest(
    CompanyId CompanyId);

public sealed record ReversePurchaseOrderApprovalHttpRequest(
    CompanyId CompanyId);

public sealed record ApprovePurchaseOrderHttpRequest(
    CompanyId CompanyId);

public sealed record IssuePurchaseOrderHttpRequest(
    CompanyId CompanyId);

public sealed record ReopenPurchaseOrderForAmendmentHttpRequest(
    CompanyId CompanyId);

public sealed record ClosePurchaseOrderHttpRequest(
    CompanyId CompanyId);

public sealed record CancelPurchaseOrderHttpRequest(
    CompanyId CompanyId);

public sealed record RefreshPurchaseOrderQuantityDiscrepanciesHttpRequest(
    CompanyId CompanyId);

public sealed record ReviewPurchaseOrderQuantityDiscrepancyHttpRequest(
    CompanyId CompanyId,
    int PurchaseOrderLineNumber,
    string DiscrepancyType,
    string InvestigationStatus,
    string? ReviewNote);

public sealed record PostReceiptDraftHttpRequest(
    CompanyId CompanyId);

public sealed record PostReceiptGrIrBridgeHttpRequest(
    CompanyId CompanyId,
    Guid? GrIrClearingAccountId,
    string? IdempotencyKey);

public sealed record ExecuteReceiptGrIrSettlementHttpRequest(
    CompanyId CompanyId,
    decimal? SettlementAmountBase,
    string? IdempotencyKey);

public sealed record PostReceiptGrIrSettlementJournalHttpRequest(
    CompanyId CompanyId,
    string? IdempotencyKey);

public sealed record SaveReceiptGrIrClearingAccountPolicyHttpRequest(
    CompanyId CompanyId,
    Guid GrIrClearingAccountId);

public sealed record PostVendorCreditHttpRequest(
    CompanyId CompanyId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveVendorCreditDraftHttpRequest(
    CompanyId CompanyId,
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
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record ReceivePaymentLookupQuery(CompanyId CompanyId);

public sealed record PostCreditApplicationHttpRequest(
    CompanyId CompanyId,
    string? IdempotencyKey);

public sealed record CreditApplicationLookupQuery(CompanyId CompanyId);

public sealed record PostPayBillHttpRequest(
    CompanyId CompanyId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record PreparePayBillDraftHttpRequest(
    CompanyId CompanyId,
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
    string? IdempotencyKey);

public sealed record VendorCreditApplicationLookupQuery(CompanyId CompanyId);

public sealed record PostFxRevaluationBatchHttpRequest(
    CompanyId CompanyId,
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
