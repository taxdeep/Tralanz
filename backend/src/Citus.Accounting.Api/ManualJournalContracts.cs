namespace Citus.Accounting.Api;

public sealed record PrepareFxRevaluationBatchHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? BookId,
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

public sealed record CompanyBookGovernanceLookupQuery(
    Guid CompanyId,
    DateOnly? AsOfDate);

public sealed record CompanyBookGovernedChangePreviewHttpRequest(
    Guid CompanyId,
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
    Guid CompanyId,
    Guid UserId,
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

public sealed record CompanyBookGovernedChangeRequestLookupQuery(Guid CompanyId);

public sealed record TransitionCompanyBookGovernedChangeRequestHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record CompanyBookGovernedChangeRequestReadinessQuery(
    Guid CompanyId,
    DateOnly? AsOfDate);

public sealed record CompanyBookGovernanceSignalsLookupQuery(
    Guid CompanyId,
    DateOnly? AsOfDate);

public sealed record CreateCompanyBookGovernanceSignalHttpRequest(
    Guid CompanyId,
    Guid UserId,
    string SignalType,
    DateOnly SignalDate,
    string? ReferenceLabel,
    string? Notes);

public sealed record RegisterCompanyBookClosedPeriodHttpRequest(
    Guid CompanyId,
    Guid UserId,
    DateOnly PeriodEndDate,
    string? ReferenceLabel,
    string? Notes);

public sealed record RegisterCompanyBookIssuedStatementHttpRequest(
    Guid CompanyId,
    Guid UserId,
    DateOnly IssuedOn,
    string StatementLabel,
    string? Notes);

public sealed record RegisterCompanyBookFiledTaxHttpRequest(
    Guid CompanyId,
    Guid UserId,
    DateOnly FiledOn,
    string FilingLabel,
    string? Notes);

public sealed record CompanyBookPolicyLookupQuery(
    Guid CompanyId,
    Guid? BookId,
    DateOnly? AsOfDate);

public sealed record PostManualJournalHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record ManualJournalLookupQuery(Guid CompanyId);

public sealed record DocumentReviewLookupQuery(Guid CompanyId);

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

public sealed record DocumentLifecycleRequestReadinessQuery(
    Guid CompanyId,
    DateOnly? AsOfDate);

public sealed record OpenItemDrillDownLookupQuery(Guid CompanyId);

public sealed record OpenItemAdjustmentPreviewLookupQuery(
    Guid CompanyId,
    string? AdjustmentType,
    DateOnly? AdjustmentDate,
    decimal? AdjustmentAmountTx);

public sealed record RequestOpenItemAdjustmentHttpRequest(
    Guid CompanyId,
    Guid? UserId,
    string AdjustmentType,
    DateOnly? AdjustmentDate,
    decimal? AdjustmentAmountTx,
    string? Reason);

public sealed record TransitionOpenItemAdjustmentRequestHttpRequest(
    Guid CompanyId,
    Guid? UserId);

public sealed record GovernOpenItemAdjustmentApprovalHttpRequest(
    Guid CompanyId,
    Guid? UserId);

public sealed record OpenItemAdjustmentRequestReadinessQuery(
    Guid CompanyId,
    DateOnly? AsOfDate);

public sealed record ExecuteOpenItemAdjustmentRequestHttpRequest(
    Guid CompanyId,
    Guid? UserId,
    Guid AdjustmentAccountId,
    DateOnly? AsOfDate,
    string? IdempotencyKey);

public sealed record OpenItemAdjustmentAccountMappingLookupQuery(
    Guid CompanyId,
    string? OpenItemType,
    string? AdjustmentType,
    bool? IncludeInactive,
    Guid? BookId,
    string? PolicyScope,
    string? SearchText,
    int? Limit);

public sealed record SaveOpenItemAdjustmentAccountMappingHttpRequest(
    Guid CompanyId,
    Guid? UserId,
    Guid? BookId,
    string OpenItemType,
    string AdjustmentType,
    Guid AdjustmentAccountId);

public sealed record DeactivateOpenItemAdjustmentAccountMappingHttpRequest(
    Guid CompanyId,
    Guid? UserId);

public sealed record SourceDocumentBrowserLookupQuery(
    Guid CompanyId,
    string? SourceType,
    string? CounterpartyRole,
    Guid? CounterpartyId,
    int? Limit);

public sealed record PostInvoiceHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveInvoiceDraftHttpRequest(
    Guid CompanyId,
    Guid UserId,
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
    IReadOnlyList<SaveInvoiceDraftLineHttpRequest> Lines);

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

public sealed record InvoiceLookupQuery(Guid CompanyId);

public sealed record PostCreditNoteHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveCreditNoteDraftHttpRequest(
    Guid CompanyId,
    Guid UserId,
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

public sealed record CreditNoteLookupQuery(Guid CompanyId);

public sealed record PostBillHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveBillDraftHttpRequest(
    Guid CompanyId,
    Guid UserId,
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

public sealed record BillLookupQuery(Guid CompanyId);

public sealed record SubmitBillDraftHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record SaveReceiptDraftHttpRequest(
    Guid CompanyId,
    Guid UserId,
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

public sealed record ReceiptLookupQuery(Guid CompanyId);

public sealed record ReceiptListQuery(Guid CompanyId, int? Take);

public sealed record SavePurchaseOrderDraftHttpRequest(
    Guid CompanyId,
    Guid UserId,
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

public sealed record PurchaseOrderLookupQuery(Guid CompanyId);

public sealed record PurchaseOrderLifecycleAuditQuery(Guid CompanyId, int? Take);

public sealed record PurchaseOrderListQuery(Guid CompanyId, int? Take);

public sealed record PurchaseOrderApprovalRequestListQuery(Guid CompanyId, int? Take, bool? IncludeClosed);

public sealed record RequestPurchaseOrderApprovalHttpRequest(
    Guid CompanyId,
    Guid UserId,
    string? Reason);

public sealed record SubmitPurchaseOrderApprovalRequestHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record RejectPurchaseOrderApprovalRequestHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record ReversePurchaseOrderApprovalHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record ApprovePurchaseOrderHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record IssuePurchaseOrderHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record ReopenPurchaseOrderForAmendmentHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record ClosePurchaseOrderHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record CancelPurchaseOrderHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record RefreshPurchaseOrderQuantityDiscrepanciesHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record ReviewPurchaseOrderQuantityDiscrepancyHttpRequest(
    Guid CompanyId,
    Guid UserId,
    int PurchaseOrderLineNumber,
    string DiscrepancyType,
    string InvestigationStatus,
    string? ReviewNote);

public sealed record PostReceiptDraftHttpRequest(
    Guid CompanyId,
    Guid UserId);

public sealed record PostReceiptGrIrBridgeHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? GrIrClearingAccountId,
    string? IdempotencyKey);

public sealed record ExecuteReceiptGrIrSettlementHttpRequest(
    Guid CompanyId,
    Guid UserId,
    decimal? SettlementAmountBase,
    string? IdempotencyKey);

public sealed record PostReceiptGrIrSettlementJournalHttpRequest(
    Guid CompanyId,
    Guid UserId,
    string? IdempotencyKey);

public sealed record SaveReceiptGrIrClearingAccountPolicyHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid GrIrClearingAccountId);

public sealed record PostVendorCreditHttpRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record SaveVendorCreditDraftHttpRequest(
    Guid CompanyId,
    Guid UserId,
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

public sealed record FxRevaluationBatchListQuery(Guid CompanyId, int? Take);

public sealed record FxRevaluationBatchLookupQuery(Guid CompanyId);
