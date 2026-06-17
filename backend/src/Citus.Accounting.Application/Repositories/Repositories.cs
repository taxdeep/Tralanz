using Citus.Accounting.Application.Queries;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Journal;

namespace Citus.Accounting.Application.Repositories;

public interface IManualJournalDocumentRepository
{
    Task<ManualJournalDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task SaveAsync(
        ManualJournalDocument document,
        CancellationToken cancellationToken);
}

public interface IInvoiceDocumentRepository
{
    Task<InvoiceDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes the next auto invoice display number (INV-######) WITHOUT
    /// reserving it — backs the create page's editable "Invoice #" default.
    /// </summary>
    Task<string> PeekNextDisplayNumberAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the posted journal entry id for an invoice (via
    /// journal_entries.source_type='invoice' + source_id), or null when the
    /// invoice has no posted JE. Backs the reverse flow.
    /// </summary>
    Task<Guid?> GetPostedJournalEntryIdAsync(
        CompanyId companyId,
        Guid invoiceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Flips a posted invoice to 'reversed' so it leaves the receivable set
    /// once its journal entry has been reversed by a compensating entry.
    /// </summary>
    Task MarkReversedAsync(
        CompanyId companyId,
        Guid invoiceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InvoiceListItem>> ListAsync(
        CompanyId companyId,
        bool includeDrafts,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        InvoiceDraftSaveModel draft,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SubmitDraftAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the distinct non-null <c>task_id</c> values across this
    /// invoice's lines. Used by the post handler's Step 5 (Task billing
    /// mark) to discover which tasks the invoice bills without dragging
    /// task semantics into <see cref="InvoiceDocument"/>. Empty when the
    /// invoice has no task-linked lines (the common case for direct-create
    /// invoices).
    /// </summary>
    Task<IReadOnlyList<Guid>> ListLinkedTaskIdsAsync(
        CompanyId companyId,
        Guid invoiceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// H6-2: returns the full per-line mapping (invoice_line_id,
    /// task_id, task_line_id) for lines that carry a task reference.
    /// The post handler uses this to drive line-level Task billing —
    /// when every task-linked line has a non-null task_line_id, the
    /// new <c>MarkLinesAsBilledAsync</c> path runs; when any task-linked
    /// line has only task_id (legacy data, draft saved before H6-2's
    /// wire-shape extension), the handler falls back to the header-level
    /// <c>MarkAsBilledAsync</c> path so nothing breaks.
    /// </summary>
    Task<IReadOnlyList<InvoiceLineTaskLink>> ListLinkedTaskLineMappingsAsync(
        CompanyId companyId,
        Guid invoiceId,
        CancellationToken cancellationToken);
}

/// <summary>
/// One row from
/// <see cref="IInvoiceDocumentRepository.ListLinkedTaskLineMappingsAsync"/>.
/// <see cref="TaskLineId"/> is null on rows that pre-date H6-2's wire-
/// shape extension; the handler treats those as header-level links and
/// routes them through the legacy <c>MarkAsBilledAsync</c> path.
/// </summary>
public sealed record class InvoiceLineTaskLink(
    Guid InvoiceLineId,
    Guid TaskId,
    Guid? TaskLineId);

public sealed record InvoiceListItem(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    Guid CustomerId,
    string CustomerName,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    DateTimeOffset? PostedAt,
    string? CustomerPoNumber = null,
    Guid? SalesOrderId = null);

public interface ICreditNoteDocumentRepository
{
    Task<CreditNoteDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CreditMemoListItem>> ListAsync(
        CompanyId companyId,
        bool includeDrafts,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        CreditNoteDraftSaveModel draft,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the distinct non-null <c>task_id</c> values across this
    /// credit note's lines. Used by the post handler's Step 2 (Task
    /// billing rollback) hook to discover which tasks the credit reverses.
    /// Empty when the credit note has no task-linked lines (the common
    /// case for standalone customer credits unrelated to a task).
    /// </summary>
    Task<IReadOnlyList<Guid>> ListLinkedTaskIdsAsync(
        CompanyId companyId,
        Guid creditNoteId,
        CancellationToken cancellationToken);

    /// <summary>
    /// H6-3: per-line task back-link lookup. Mirror of the invoice
    /// repo method. The post handler uses the returned rows to drive
    /// line-level Task rollback — lines with task_line_id go to the
    /// new <c>RollbackLinesAsync</c> path; task_id-only rows fall
    /// back to the legacy whole-task rollback.
    /// </summary>
    Task<IReadOnlyList<CreditNoteLineTaskLink>> ListLinkedTaskLineMappingsAsync(
        CompanyId companyId,
        Guid creditNoteId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Credit-note analog of <see cref="InvoiceLineTaskLink"/>. The post
/// handler reads these rows and (per row) either releases a specific
/// task_line via the H6-2 line-level path or whole-task-rollbacks
/// via the legacy path.
/// </summary>
public sealed record class CreditNoteLineTaskLink(
    Guid CreditNoteLineId,
    Guid TaskId,
    Guid? TaskLineId);

// Surfaced as "credit memo" on the frontend even though the GL artifact
// is a credit_note — same QBO-flavoured operator label split that the
// /credit-memos endpoint uses.
public sealed record CreditMemoListItem(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DocumentDate,
    Guid CustomerId,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    DateTimeOffset? PostedAt,
    string? CustomerPoNumber = null);

public interface IBillDocumentRepository
{
    Task<BillDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        BillDraftSaveModel draft,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SubmitDraftAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> CancelSubmittedAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Flips a posted bill to 'reversed' after its compensation JE posts.
    /// Mirror of <see cref="IInvoiceDocumentRepository.MarkReversedAsync"/>.
    /// </summary>
    Task MarkReversedAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}

public interface IVendorCreditDocumentRepository
{
    Task<VendorCreditDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<VendorCreditListItem>> ListAsync(
        CompanyId companyId,
        bool includeDrafts,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        VendorCreditDraftSaveModel draft,
        CancellationToken cancellationToken);
}

public sealed record VendorCreditListItem(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DocumentDate,
    Guid VendorId,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    DateTimeOffset? PostedAt);

public interface IReceiptDocumentRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<ReceiptDocument?> GetAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReceiptDocumentListItem>> ListAsync(
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        ReceiptDraftSaveModel draft,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> PostAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken);
}

public interface IPurchaseOrderDocumentRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<PurchaseOrderDocument?> GetAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PurchaseOrderDocumentListItem>> ListAsync(
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        PurchaseOrderDraftSaveModel draft,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> ApproveAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> IssueAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> ReopenForAmendmentAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> CloseAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> CancelAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PurchaseOrderLifecycleAuditEntry>> ListLifecycleAuditAsync(
        CompanyId companyId,
        Guid documentId,
        int take,
        CancellationToken cancellationToken);

    Task<PurchaseOrderApprovalRequestTransitionResult> RequestApprovalAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        string? reason,
        CancellationToken cancellationToken);

    Task<PurchaseOrderApprovalRequestRecord?> GetLatestApprovalRequestAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PurchaseOrderApprovalRequestRecord>> ListApprovalRequestsAsync(
        CompanyId companyId,
        int take,
        bool includeClosed,
        CancellationToken cancellationToken);

    Task<PurchaseOrderApprovalRequestTransitionResult?> SubmitApprovalRequestAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken);

    Task<PurchaseOrderApprovalRequestTransitionResult?> RejectApprovalRequestAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> ReverseApprovalAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task ValidateBillAnchorsForPostingAsync(
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken);

    Task<PurchaseOrderThreeQuantitySummary?> GetThreeQuantitySummaryAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken);

    Task<PurchaseOrderPurchaseVarianceSummary> GetPurchaseVarianceSummaryAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, PurchaseOrderThreeQuantitySummary>> GetThreeQuantitySummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> purchaseOrderIds,
        CancellationToken cancellationToken);

    Task<PurchaseOrderThreeQuantitySummary?> RefreshQuantityDiscrepanciesAsync(
        CompanyId companyId,
        UserId userId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken);

    Task<PurchaseOrderThreeQuantitySummary?> ReviewQuantityDiscrepancyAsync(
        CompanyId companyId,
        UserId userId,
        Guid purchaseOrderId,
        int purchaseOrderLineNumber,
        string discrepancyType,
        string investigationStatus,
        string? reviewNote,
        CancellationToken cancellationToken);
}

public interface IBillReceiptMatchingRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<BillReceiptMatchingLaneSummary> GetBillLaneSummaryAsync(
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, BillReceiptPostingGateSnapshot>> GetBillPostingGateSnapshotsAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> billDocumentIds,
        CancellationToken cancellationToken);
}

public static class PurchaseOrderApprovalThresholdPolicy
{
    public const decimal TemporaryGovernanceThresholdAmount = 10_000m;

    public static bool RequiresGovernanceApproval(decimal? estimatedOrderAmount) =>
        estimatedOrderAmount.HasValue &&
        estimatedOrderAmount.Value > TemporaryGovernanceThresholdAmount;
}

public sealed record SourceDocumentDraftSaveResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    string Status);

public sealed record InvoiceDraftSaveModel(
    Guid? DocumentId,
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
    IReadOnlyList<InvoiceDraftLineSaveModel> Lines,
    string? CustomerPoNumber = null,
    Guid? SalesOrderId = null,
    // Optimistic-concurrency token. The editor holds the updated_at it
    // saw on load and passes it back on save; the repository rejects
    // the UPDATE if the row's current updated_at no longer matches —
    // see ConcurrencyConflictException. Null on first save (insert
    // path) or when the caller opts out of the check.
    DateTimeOffset? ExpectedUpdatedAt = null,
    // User-supplied invoice number (free-form). When non-blank on a NEW
    // invoice the repository uses it as the display number instead of
    // auto-reserving the next INV-######; ignored on update (the number
    // is already assigned). Uniqueness is enforced per company.
    string? InvoiceNumber = null,
    // Free-text billing / shipping address surfaced on the invoice Header.
    string? BillingAddress = null,
    string? ShippingAddress = null);

public sealed record InvoiceDraftLineSaveModel(
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
    // Per-line back-link to the Task this line bills. Persists into
    // invoice_lines.task_id; the post handler aggregates distinct
    // task_ids to flip source tasks Completed -> Billed on post.
    Guid? TaskId = null,
    // H6-2: when present, the post handler stamps THIS specific
    // task_line as billed and recomputes the task header status
    // (Open|Completed -> PartiallyBilled, or -> Billed when this is
    // the final un-billed line). Null falls back to the legacy
    // whole-task marking via task_id alone.
    Guid? TaskLineId = null,
    // R4-sales: tax_code_sets.id — a Tax Code bundle on this line. When set,
    // the engine expands it to its member Rules (multi-tax); otherwise the
    // single Rule in TaxCodeId is used.
    Guid? TaxCodeSetId = null);

public sealed record CreditNoteDraftSaveModel(
    Guid? DocumentId,
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
    IReadOnlyList<CreditNoteDraftLineSaveModel> Lines,
    string? CustomerPoNumber = null);

public sealed record CreditNoteDraftLineSaveModel(
    int LineNumber,
    Guid RevenueAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    decimal TaxAmount,
    // Per-line back-link to the Task the credit reverses. Persists into
    // credit_note_lines.task_id; the post handler rolls these tasks
    // back to Completed.
    Guid? TaskId = null,
    // H6-3: optional pin to a specific task_lines row that this credit
    // line releases. When present, the post handler routes through
    // RollbackLinesAsync (per-line audit). Null falls back to legacy
    // whole-task rollback via TaskId alone.
    Guid? TaskLineId = null);

public sealed record BillDraftSaveModel(
    Guid? DocumentId,
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
    IReadOnlyList<BillDraftLineSaveModel> Lines,
    Guid? PaymentTermId = null,
    Guid? SourcePurchaseOrderId = null,
    string? SourcePurchaseOrderNumber = null,
    string? BillNumber = null);

public sealed record BillDraftLineSaveModel(
    int LineNumber,
    Guid ExpenseAccountId,
    string Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal TaxAmount,
    bool IsTaxRecoverable,
    Guid? ItemId = null,
    Guid? WarehouseId = null,
    string? UomCode = null,
    decimal? Quantity = null,
    decimal? UnitCost = null,
    Guid? PurchaseOrderId = null,
    int? PurchaseOrderLineNumber = null,
    Guid? TaxCodeSetId = null,
    Guid? TaskId = null);

public sealed record VendorCreditDraftSaveModel(
    Guid? DocumentId,
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
    IReadOnlyList<VendorCreditDraftLineSaveModel> Lines);

public sealed record VendorCreditDraftLineSaveModel(
    int LineNumber,
    Guid ExpenseAccountId,
    string Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal TaxAmount,
    bool IsTaxRecoverable);

public sealed record ReceiptDraftSaveModel(
    Guid? DocumentId,
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    Guid WarehouseId,
    DateOnly ReceiptDate,
    string? VendorReference,
    string? SourceReference,
    string? Memo,
    IReadOnlyList<ReceiptDraftLineSaveModel> Lines);

public sealed record ReceiptDraftLineSaveModel(
    int LineNumber,
    Guid ItemId,
    decimal Quantity,
    string UomCode,
    string? TrackingCaptureHome,
    Guid? PurchaseOrderId = null,
    int? PurchaseOrderLineNumber = null);

public sealed record PurchaseOrderDraftSaveModel(
    Guid? DocumentId,
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    string? VendorReference,
    string? Memo,
    IReadOnlyList<PurchaseOrderDraftLineSaveModel> Lines);

public sealed record PurchaseOrderDraftLineSaveModel(
    int LineNumber,
    Guid ItemId,
    decimal OrderedQuantity,
    string UomCode,
    string? Description = null,
    decimal? UnitCost = null);

public sealed record ReceiptDocumentListItem(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    Guid VendorId,
    Guid WarehouseId,
    DateOnly ReceiptDate,
    int LineCount,
    decimal TotalQuantity,
    string? VendorReference,
    string? SourceReference,
    string? Memo,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PostedAt);

public sealed record PurchaseOrderDocumentListItem(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    Guid VendorId,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    int LineCount,
    decimal TotalOrderedQuantity,
    string? VendorReference,
    string? Memo,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? AmendmentStartedAt);

public sealed record PurchaseOrderLifecycleAuditEntry(
    Guid AuditId,
    Guid PurchaseOrderId,
    string Action,
    string ActorType,
    UserId? ActorId,
    string? FromStatus,
    string? ToStatus,
    string? EntityNumber,
    string? DisplayNumber,
    DateTimeOffset CreatedAt);

public sealed record PurchaseOrderApprovalRequestRecord(
    Guid RequestId,
    Guid PurchaseOrderId,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string PurchaseOrderStatus,
    decimal? EstimatedAmount,
    decimal ThresholdAmount,
    bool RequiresGovernanceApproval,
    string RequestStatus,
    string ApprovalStatus,
    string RequestedByActorType,
    UserId? RequestedByActorId,
    DateTimeOffset RequestedAt,
    string? SubmittedByActorType,
    UserId? SubmittedByActorId,
    DateTimeOffset? SubmittedAt,
    string? RejectedByActorType,
    UserId? RejectedByActorId,
    DateTimeOffset? RejectedAt,
    string? Reason);

public sealed record PurchaseOrderApprovalRequestTransitionResult(
    PurchaseOrderApprovalRequestRecord Request,
    string TransitionCode,
    string OutcomeCode,
    string Message);

public sealed record PurchaseOrderLineThreeQuantitySummary(
    int LineNumber,
    Guid ItemId,
    string UomCode,
    decimal OrderedQuantity,
    decimal ReceivedQuantity,
    decimal BilledQuantity,
    decimal RemainingToReceiveQuantity,
    decimal RemainingToBillQuantity,
    string QuantityStatus);

public sealed record PurchaseOrderQuantityDiscrepancySummary(
    Guid PurchaseOrderId,
    int PurchaseOrderLineNumber,
    string DiscrepancyType,
    string InvestigationStatus,
    Guid ItemId,
    string UomCode,
    decimal OrderedQuantity,
    decimal ReceivedQuantity,
    decimal BilledQuantity,
    decimal RemainingToReceiveQuantity,
    decimal RemainingToBillQuantity,
    string Summary,
    DateTimeOffset FirstDetectedAt,
    DateTimeOffset LastDetectedAt,
    string? ReviewNote = null,
    UserId? ReviewedByUserId = null,
    DateTimeOffset? ReviewedAt = null);

public sealed record PurchaseOrderThreeQuantitySummary(
    Guid PurchaseOrderId,
    int LineCount,
    decimal OrderedQuantity,
    decimal ReceivedQuantity,
    decimal BilledQuantity,
    decimal RemainingToReceiveQuantity,
    decimal RemainingToBillQuantity,
    int OverReceivedLineCount,
    int OverBilledLineCount,
    int BilledAheadOfReceivedLineCount,
    int OpenDiscrepancyCount,
    string QuantityStatus,
    IReadOnlyList<PurchaseOrderLineThreeQuantitySummary> Lines,
    IReadOnlyList<PurchaseOrderQuantityDiscrepancySummary> Discrepancies);

public sealed record PurchaseOrderPurchaseVarianceSummary(
    Guid PurchaseOrderId,
    int VarianceLineCount,
    int CandidateLineCount,
    int NoVarianceLineCount,
    int BlockedLineCount,
    string VarianceStatus,
    decimal CandidateVarianceAmountBase,
    bool CanRequestPosting,
    string PostingReadinessStatus,
    string PostingReadinessReason,
    DateTimeOffset? LastRefreshedAt);

public static class PurchaseOrderPurchaseVariancePostingReadinessPolicy
{
    public const string NotApplicable = "not_applicable";
    public const string NoVariance = "no_variance";
    public const string ReadyForPosting = "ready_for_posting";
    public const string Blocked = "blocked";

    public static string ResolveStatus(
        int varianceLineCount,
        int candidateLineCount,
        int blockedLineCount)
    {
        if (varianceLineCount <= 0)
        {
            return NotApplicable;
        }

        if (blockedLineCount > 0)
        {
            return Blocked;
        }

        return candidateLineCount > 0 ? ReadyForPosting : NoVariance;
    }

    public static bool CanRequestPosting(string readinessStatus) =>
        string.Equals(readinessStatus, ReadyForPosting, StringComparison.Ordinal);

    public static string BuildReason(
        string readinessStatus,
        int candidateLineCount,
        int blockedLineCount,
        decimal candidateVarianceAmountBase) =>
        readinessStatus switch
        {
            ReadyForPosting => $"{candidateLineCount} purchase variance candidate line(s) totaling {candidateVarianceAmountBase:N2} base are ready for a future explicit PPV posting/disposition command.",
            Blocked => $"{blockedLineCount} purchase variance line(s) are still blocked by GR/IR settlement, settlement journal, AP clearing, Bill posting, or quantity-basis prerequisites.",
            NoVariance => "Downstream GR/IR/AP variance control exists and currently reports no purchase variance to post.",
            _ => "No downstream purchase variance control lines are currently attached to this purchase order."
        };
}

public static class PurchaseOrderThreeQuantityStatusPolicy
{
    public const string NotApplicable = "not_applicable";
    public const string OrderedOnly = "ordered_only";
    public const string PartiallyReceived = "partially_received";
    public const string FullyReceived = "fully_received";
    public const string PartiallyBilled = "partially_billed";
    public const string FullyBilled = "fully_billed";
    public const string OverReceived = "over_received";
    public const string OverBilled = "over_billed";
    public const string BilledAheadOfReceived = "billed_ahead_of_received";
    public const string Inconsistent = "inconsistent";

    public static string ResolveLineStatus(
        decimal orderedQuantity,
        decimal receivedQuantity,
        decimal billedQuantity)
    {
        if (orderedQuantity <= 0m)
        {
            return Inconsistent;
        }

        if (receivedQuantity > orderedQuantity)
        {
            return OverReceived;
        }

        if (billedQuantity > orderedQuantity)
        {
            return OverBilled;
        }

        if (billedQuantity > receivedQuantity)
        {
            return BilledAheadOfReceived;
        }

        if (billedQuantity == orderedQuantity)
        {
            return FullyBilled;
        }

        if (billedQuantity > 0m)
        {
            return PartiallyBilled;
        }

        if (receivedQuantity == orderedQuantity)
        {
            return FullyReceived;
        }

        return receivedQuantity > 0m ? PartiallyReceived : OrderedOnly;
    }

    public static string ResolveSummaryStatus(
        int lineCount,
        int overReceivedLineCount,
        int overBilledLineCount,
        int billedAheadOfReceivedLineCount,
        decimal orderedQuantity,
        decimal receivedQuantity,
        decimal billedQuantity)
    {
        if (lineCount <= 0)
        {
            return NotApplicable;
        }

        if (overReceivedLineCount > 0)
        {
            return OverReceived;
        }

        if (overBilledLineCount > 0)
        {
            return OverBilled;
        }

        if (billedAheadOfReceivedLineCount > 0 || billedQuantity > receivedQuantity)
        {
            return BilledAheadOfReceived;
        }

        if (orderedQuantity <= 0m)
        {
            return Inconsistent;
        }

        if (billedQuantity == orderedQuantity)
        {
            return FullyBilled;
        }

        if (billedQuantity > 0m)
        {
            return PartiallyBilled;
        }

        if (receivedQuantity == orderedQuantity)
        {
            return FullyReceived;
        }

        return receivedQuantity > 0m ? PartiallyReceived : OrderedOnly;
    }
}

public sealed record BillReceiptPostingGateSnapshot(
    Guid BillDocumentId,
    int BillInboundLineCount,
    decimal BillInboundQuantity,
    int ReceiptCount,
    decimal CoveredQuantity,
    decimal RemainingQuantity,
    string MatchStatus,
    DateTimeOffset? LatestReceiptPostedAt,
    int OpenDiscrepancyCount);

public sealed record BillReceiptMatchingReceiptSummary(
    Guid ReceiptDocumentId,
    string DisplayNumber,
    DateOnly ReceiptDate,
    string Status,
    decimal ReceiptQuantity,
    decimal MatchedQuantity,
    string? VendorReference,
    string? SourceReference,
    DateTimeOffset? PostedAt);

public sealed record BillReceiptMatchingLineSummary(
    int BillLineNumber,
    Guid ItemId,
    string ItemCode,
    string ItemName,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    string UomCode,
    decimal BillQuantity,
    decimal CoveredQuantity,
    decimal RemainingQuantity,
    int ReceiptCount,
    string MatchStatus);

public sealed record BillReceiptMatchingDiscrepancySummary(
    Guid BillDocumentId,
    int BillLineNumber,
    string DiscrepancyType,
    string InvestigationStatus,
    Guid ItemId,
    string ItemCode,
    string ItemName,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    string UomCode,
    decimal BillQuantity,
    decimal CoveredQuantity,
    decimal RemainingQuantity,
    string Summary,
    DateTimeOffset FirstDetectedAt,
    DateTimeOffset LastDetectedAt);

public sealed record BillReceiptMatchingLaneSummary(
    Guid BillDocumentId,
    int BillInboundLineCount,
    decimal BillInboundQuantity,
    int ReceiptCount,
    decimal CoveredQuantity,
    decimal RemainingQuantity,
    string MatchStatus,
    DateTimeOffset? LatestReceiptPostedAt,
    IReadOnlyList<BillReceiptMatchingReceiptSummary> RecentReceipts,
    IReadOnlyList<BillReceiptMatchingLineSummary> LineSummaries,
    IReadOnlyList<BillReceiptMatchingDiscrepancySummary> Discrepancies);

public interface IReceivePaymentDocumentRepository
{
    Task<ReceivePaymentDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SettlementOpenItemCandidate>> ListOpenReceivableCandidatesAsync(
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task<SettlementDraftPreparationResult> PrepareDraftAsync(
        ReceivePaymentDraftPreparation request,
        CancellationToken cancellationToken);
}

public interface ICreditApplicationDocumentRepository
{
    Task<CreditApplicationDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}

public interface IPayBillDocumentRepository
{
    Task<PayBillDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SettlementOpenItemCandidate>> ListOpenPayableCandidatesAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken);

    Task<SettlementDraftPreparationResult> PrepareDraftAsync(
        PayBillDraftPreparation request,
        CancellationToken cancellationToken);
}

public sealed record SettlementOpenItemCandidate(
    Guid OpenItemId,
    string SourceType,
    Guid SourceDocumentId,
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

public sealed record SettlementDraftLine(
    Guid TargetOpenItemId,
    decimal AppliedAmountTx);

public sealed record ReceivePaymentDraftPreparation(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    Guid? AcceptedFxSnapshotId,
    string? Memo,
    IReadOnlyList<SettlementDraftLine> Lines,
    /// <summary>
    /// Slice of cash deposited that wasn't applied to any AR open item
    /// and is being parked as a Customer Deposit (future credit on this
    /// customer). The repository validates
    /// <c>sum(Lines) + ExtraDepositAmount == cash_deposited</c>, then
    /// creates a customer_deposits row + the matching ar_open_items
    /// row in the same transaction. Default 0 — when the form's Total
    /// to Bank exactly matches Applied total, no deposit is created.
    /// </summary>
    decimal ExtraDepositAmount = 0m);

public sealed record PayBillDraftPreparation(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    Guid? AcceptedFxSnapshotId,
    string? Memo,
    IReadOnlyList<SettlementDraftLine> Lines);

public sealed record SettlementDraftPreparationResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    int PreparedLineCount,
    decimal TotalAmount,
    string Status);

public interface IVendorCreditApplicationDocumentRepository
{
    Task<VendorCreditApplicationDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}

public sealed record FxRevaluationDraftPreparation(
    CompanyId CompanyId,
    UserId UserId,
    Guid? BookId,
    DateOnly RevaluationDate,
    CurrencyCode TransactionCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    bool IncludeAccountsReceivable,
    bool IncludeAccountsPayable,
    string? Memo);

public sealed record FxRevaluationUnwindPreparation(
    CompanyId CompanyId,
    UserId UserId,
    Guid ReversalOfDocumentId,
    DateOnly UnwindDate,
    string? Memo);

public sealed record FxRevaluationDraftPreparationResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    Guid? BookId,
    string? BookCode,
    string? AccountingStandard,
    string? RevaluationProfile,
    string? FxRoundingPolicy,
    int PreparedLineCount,
    string Status);

public sealed record FxRevaluationCascadeUnwindPlanStep(
    Guid DocumentId,
    string DisplayNumber,
    DateOnly RevaluationDate,
    DateTimeOffset PostedAt,
    bool IsRequestedBatch,
    bool IsNextStep);

public sealed record FxRevaluationCascadeUnwindPlanResult(
    Guid RequestedDocumentId,
    string RequestedDisplayNumber,
    Guid NextDocumentId,
    string NextDisplayNumber,
    bool RequestedBatchIsTail,
    IReadOnlyList<FxRevaluationCascadeUnwindPlanStep> ActiveRevaluationChain);

public sealed record FxRevaluationBatchListItem(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    string BatchKind,
    Guid? ReversalOfDocumentId,
    Guid? BookId,
    string? BookCode,
    string? AccountingStandard,
    string? RevaluationProfile,
    string? FxRoundingPolicy,
    DateOnly DocumentDate,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    Guid? FxSnapshotId,
    decimal FxRate,
    int LineCount,
    decimal UnrealizedTotalBase,
    Guid? LinkedJournalEntryId,
    string? LinkedJournalEntryDisplayNumber,
    DateTimeOffset? LinkedJournalPostedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IFxRevaluationDocumentRepository
{
    Task<FxRevaluationDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FxRevaluationBatchListItem>> ListRecentAsync(
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken);

    Task<FxRevaluationCascadeUnwindPlanResult> GetCascadeUnwindPlanAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<FxRevaluationDraftPreparationResult> PrepareDraftAsync(
        FxRevaluationDraftPreparation request,
        CancellationToken cancellationToken);

    Task<FxRevaluationDraftPreparationResult> PrepareNextPeriodUnwindDraftAsync(
        FxRevaluationUnwindPreparation request,
        CancellationToken cancellationToken);
}

public interface IJournalEntryRepository
{
    Task<bool> ExistsByIdempotencyKeyAsync(
        CompanyId companyId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task SaveAsync(
        JournalEntry entry,
        CancellationToken cancellationToken);
}

public interface IJournalEntryReviewRepository
{
    Task<IReadOnlyList<JournalEntryReviewListItem>> ListRecentAsync(
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken);

    Task<JournalEntryReview?> GetAsync(
        CompanyId companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken);

    Task<JournalEntryReviewListItem?> FindBySourceAsync(
        CompanyId companyId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken);
}

public sealed record JournalEntryReviewListItem(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    string SourceType,
    Guid SourceId,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal TotalTxDebit,
    decimal TotalTxCredit,
    decimal TotalDebit,
    decimal TotalCredit,
    int LineCount,
    DateOnly? EntryDate,
    DateTimeOffset? PostedAt,
    DateTimeOffset? VoidedAt,
    DateTimeOffset? ReversedAt);

public sealed record JournalEntryReview(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    string SourceType,
    Guid SourceId,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal ExchangeRate,
    DateOnly ExchangeRateDate,
    string ExchangeRateSource,
    Guid? FxRateSnapshotId,
    decimal TotalTxDebit,
    decimal TotalTxCredit,
    decimal TotalDebit,
    decimal TotalCredit,
    int LineCount,
    DateTimeOffset? PostedAt,
    DateTimeOffset? VoidedAt,
    DateTimeOffset? ReversedAt,
    UserId CreatedByUserId,
    IReadOnlyList<JournalEntryReviewLine> Lines);

public sealed record JournalEntryReviewLine(
    Guid LineId,
    int LineNumber,
    Guid AccountId,
    string AccountCode,
    string AccountName,
    string RootType,
    string DetailType,
    string Description,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit,
    string? TaxComponentType,
    string? ControlRole,
    Guid? PartyId,
    string? PostingRole,
    int? SourceLineNumber);

public interface IAccountingReportRepository
{
    Task<TrialBalanceReport?> GetTrialBalanceAsync(
        GetTrialBalanceQuery query,
        CancellationToken cancellationToken);

    Task<IncomeStatementReport?> GetIncomeStatementAsync(
        GetIncomeStatementQuery query,
        CancellationToken cancellationToken);

    Task<BalanceSheetReport?> GetBalanceSheetAsync(
        GetBalanceSheetQuery query,
        CancellationToken cancellationToken);

    Task<ArAgingReport?> GetArAgingAsync(
        GetArAgingQuery query,
        CancellationToken cancellationToken);

    Task<ApAgingReport?> GetApAgingAsync(
        GetApAgingQuery query,
        CancellationToken cancellationToken);

    Task<JournalReport?> GetJournalReportAsync(
        GetJournalReportQuery query,
        CancellationToken cancellationToken);

    Task<SalesCashFlowReport?> GetSalesCashFlowAsync(
        GetSalesCashFlowQuery query,
        CancellationToken cancellationToken);

    Task<IncomeOverTimeReport?> GetIncomeOverTimeAsync(
        GetIncomeOverTimeQuery query,
        CancellationToken cancellationToken);

    Task<ExpenseCashOutflowReport?> GetExpenseCashOutflowAsync(
        GetExpenseCashOutflowQuery query,
        CancellationToken cancellationToken);

    Task<ExpenseOverTimeReport?> GetExpenseOverTimeAsync(
        GetExpenseOverTimeQuery query,
        CancellationToken cancellationToken);
}

public interface IAccountingDocumentReviewRepository
{
    Task<AccountingDocumentReview?> GetSourceDocumentAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecyclePreview?> GetLifecyclePreviewAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleActionPreview?> GetLifecycleActionPreviewAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        string actionCode,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleCommandAttempt?> AttemptVoidAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleCommandAttempt?> AttemptReverseAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleRequestRecord?> GetReverseRequestAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleRequestRecord?> GetLatestReverseRequestAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleRequestTransitionResult?> SubmitReverseRequestAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleRequestTransitionResult?> CancelReverseRequestAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleRequestReadiness?> GetReverseRequestApplyReadinessAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleRequestExecutionResult?> ExecuteReverseRequestAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        UserId? actorId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleRequestExecutionResult?> CompleteReverseRequestExecutionAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        UserId? actorId,
        Guid compensationJournalEntryId,
        string compensationJournalEntryDisplayNumber,
        string compensationSourceType,
        DateTimeOffset executedAt,
        CancellationToken cancellationToken);

    Task<AccountingDocumentLifecycleExecutionPlan?> GetReverseRequestExecutionPlanAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountingDocumentSubledgerReverseBlocker>> ListSubledgerReverseBlockersAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountingDocumentSettlementApplicationReversal>> ListSettlementApplicationReversalsAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountingSourceDocumentListItem>> ListSourceDocumentsAsync(
        CompanyId companyId,
        string? sourceType,
        string? counterpartyRole,
        Guid? counterpartyId,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record AccountingDocumentReview(
    string SourceType,
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string CounterpartyRole,
    Guid? CounterpartyId,
    Guid? ControlAccountId,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    string? JournalEntryStatus,
    DateTimeOffset? JournalEntryPostedAt,
    DateTimeOffset? JournalEntryVoidedAt,
    DateTimeOffset? JournalEntryReversedAt,
    string LifecycleMode,
    bool CanEditDraft,
    bool CanPostDraft,
    string LifecycleReason,
    IReadOnlyList<AccountingDocumentLifecycleAction> LifecycleActions,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal SubtotalAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    IReadOnlyList<AccountingDocumentReviewLine> Lines);

public sealed record AccountingDocumentReviewLine(
    int LineNumber,
    Guid AccountId,
    string AccountCode,
    string AccountName,
    string Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal LineAmount,
    decimal TaxAmount,
    bool? IsTaxRecoverable,
    Guid? TaxAccountId,
    decimal? TxDebit,
    decimal? TxCredit,
    Guid? SourceOpenItemId,
    string? SourceDocumentType,
    Guid? SourceDocumentId,
    string? SourceDocumentDisplayNumber,
    Guid? TargetOpenItemId,
    string? TargetDocumentType,
    Guid? TargetDocumentId,
    string? TargetDocumentDisplayNumber);

public sealed record AccountingSourceDocumentListItem(
    string SourceType,
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string CounterpartyRole,
    Guid? CounterpartyId,
    string? CounterpartyDisplayName,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal TotalAmount,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    string? JournalEntryStatus,
    DateTimeOffset? JournalEntryPostedAt,
    DateTimeOffset? JournalEntryVoidedAt,
    DateTimeOffset? JournalEntryReversedAt);

public sealed record AccountingDocumentLifecycleAction(
    string ActionCode,
    string ActionLabel,
    string AvailabilityMode,
    bool IsAvailable,
    string Reason);

public sealed record AccountingDocumentLifecyclePreview(
    string SourceType,
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    string? JournalEntryStatus,
    DateTimeOffset? JournalEntryPostedAt,
    DateTimeOffset? JournalEntryVoidedAt,
    DateTimeOffset? JournalEntryReversedAt,
    string LifecycleMode,
    bool CanEditDraft,
    bool CanPostDraft,
    string LifecycleReason,
    IReadOnlyList<AccountingDocumentLifecycleAction> LifecycleActions);

public sealed record AccountingDocumentLifecycleActionPreview(
    string SourceType,
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    string? JournalEntryStatus,
    DateTimeOffset? JournalEntryPostedAt,
    DateTimeOffset? JournalEntryVoidedAt,
    DateTimeOffset? JournalEntryReversedAt,
    string LifecycleMode,
    bool CanEditDraft,
    bool CanPostDraft,
    string LifecycleReason,
    string ActionCode,
    string ActionLabel,
    string AvailabilityMode,
    bool IsAvailable,
    string Reason);

public sealed record AccountingDocumentLifecycleCommandAttempt(
    string SourceType,
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    string? JournalEntryStatus,
    string LifecycleMode,
    string ActionCode,
    string ActionLabel,
    string AvailabilityMode,
    string ExecutionMode,
    bool CommandAccepted,
    bool Executed,
    Guid? RequestId,
    bool Persisted,
    string OutcomeCode,
    string Message);

public sealed record AccountingDocumentLifecycleRequestRecord(
    Guid RequestId,
    CompanyId CompanyId,
    string SourceType,
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    string? JournalEntryStatus,
    string LifecycleMode,
    string ActionCode,
    string ActionLabel,
    string AvailabilityMode,
    bool IsAvailable,
    string Reason,
    string RequestStatus,
    string RequestedByActorType,
    UserId? RequestedByActorId,
    DateTimeOffset RequestedAt,
    string? SubmittedByActorType,
    UserId? SubmittedByActorId,
    DateTimeOffset? SubmittedAt,
    string? CancelledByActorType,
    UserId? CancelledByActorId,
    DateTimeOffset? CancelledAt,
    string ExecutionStatus,
    string? ExecutionRequestedByActorType,
    UserId? ExecutionRequestedByActorId,
    DateTimeOffset? ExecutionRequestedAt,
    string? ExecutionCompletedByActorType,
    UserId? ExecutionCompletedByActorId,
    DateTimeOffset? ExecutionCompletedAt,
    Guid? CompensationJournalEntryId,
    string? CompensationJournalEntryDisplayNumber,
    string? CompensationSourceType);

public sealed record AccountingDocumentLifecycleRequestTransitionResult(
    AccountingDocumentLifecycleRequestRecord Request,
    string TransitionCode,
    string OutcomeCode,
    string Message);

public sealed record AccountingDocumentLifecycleRequestReadiness(
    AccountingDocumentLifecycleRequestRecord Request,
    DateOnly AsOfDate,
    bool GovernanceReady,
    bool ApplyReady,
    string ExecutionMode,
    string AvailabilityMode,
    bool IsAvailable,
    string Reason);

public sealed record AccountingDocumentLifecycleRequestExecutionResult(
    AccountingDocumentLifecycleRequestRecord Request,
    DateOnly AsOfDate,
    string ExecutionMode,
    bool CommandAccepted,
    bool Executed,
    bool Persisted,
    string OutcomeCode,
    string Message,
    Guid? CompensationJournalEntryId,
    string? CompensationJournalEntryDisplayNumber,
    string? CompensationSourceType);

public sealed record AccountingDocumentLifecycleExecutionPlan(
    AccountingDocumentLifecycleRequestRecord Request,
    DateOnly AsOfDate,
    string ExecutionMode,
    bool CanExecute,
    string OverallStatus,
    string Reason,
    IReadOnlyList<AccountingDocumentLifecycleExecutionPlanStep> Steps);

public sealed record AccountingDocumentLifecycleExecutionPlanStep(
    int StepNumber,
    string StepCode,
    string StepLabel,
    string StepStatus,
    string Reason);

public sealed record AccountingDocumentSubledgerReverseBlocker(
    Guid SettlementApplicationId,
    string ApplicationType,
    string SettlementSourceType,
    Guid SettlementSourceId,
    string SettlementSourceDisplayNumber,
    DateOnly? SettlementSourceDocumentDate,
    string TargetOpenItemType,
    Guid TargetOpenItemId,
    string TargetSourceType,
    Guid TargetSourceId,
    string TargetSourceDisplayNumber,
    decimal AppliedAmountTx,
    decimal AppliedAmountBase,
    decimal? SettlementFxRate,
    decimal? RealizedFxAmount,
    DateTimeOffset AppliedAt,
    Guid? ReverseRequestId,
    string ReverseRequestStatus,
    string ReverseExecutionStatus,
    DateTimeOffset? ReverseRequestedAt);

public sealed record AccountingDocumentSettlementApplicationReversal(
    Guid ReversalEventId,
    Guid RequestId,
    Guid SettlementApplicationId,
    string ApplicationType,
    string SourceType,
    Guid SourceId,
    string TargetOpenItemType,
    Guid TargetOpenItemId,
    decimal AppliedAmountTx,
    decimal AppliedAmountBase,
    decimal? SettlementFxRate,
    decimal? RealizedFxAmount,
    DateTimeOffset OriginalApplicationCreatedAt,
    Guid? OriginalApplicationCreatedByUserId,
    DateTimeOffset ReversedAt,
    string ReversedByActorType,
    UserId? ReversedByActorId,
    string ReversalMode);

public sealed record OpenItemDrillDown(
    Guid OpenItemId,
    string OpenItemType,
    CompanyId CompanyId,
    string PartyRole,
    Guid PartyId,
    string PartyEntityNumber,
    string PartyDisplayName,
    string SourceType,
    Guid SourceDocumentId,
    string SourceDocumentDisplayNumber,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    string BalanceSide,
    string Status,
    decimal OriginalAmountTx,
    decimal OriginalAmountBase,
    decimal OpenAmountTx,
    decimal OpenAmountBase);

public sealed record OpenItemApplicationDrillDown(
    Guid ApplicationId,
    string ApplicationType,
    string SourceType,
    Guid SourceDocumentId,
    string SourceDocumentDisplayNumber,
    DateOnly SourceDocumentDate,
    decimal AppliedAmountTx,
    decimal AppliedAmountBase,
    decimal? SettlementFxRate,
    decimal? RealizedFxAmount,
    DateTimeOffset CreatedAt);

public interface IFxSnapshotRepository
{
    Task<FxSnapshotRef?> FindAcceptedSnapshotAsync(
        CompanyId companyId,
        CurrencyCode baseCurrencyCode,
        CurrencyCode quoteCurrencyCode,
        DateOnly requestedDate,
        Guid? snapshotId,
        CancellationToken cancellationToken);
}

public interface IOpenItemAdjustmentAccountMappingRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<OpenItemAdjustmentAccountMappingLookupResult> LookupAsync(
        OpenItemAdjustmentAccountMappingLookupRequest request,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentAccountMappingSaveResult> SaveAsync(
        OpenItemAdjustmentAccountMappingSaveRequest request,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentAccountMappingTransitionResult?> DeactivateAsync(
        CompanyId companyId,
        Guid mappingId,
        UserId? actorId,
        CancellationToken cancellationToken);
}

public sealed record OpenItemAdjustmentAccountMappingRecord(
    Guid MappingId,
    CompanyId CompanyId,
    Guid? BookId,
    string? BookCode,
    string? AccountingStandard,
    string OpenItemType,
    string AdjustmentType,
    Guid AdjustmentAccountId,
    string AdjustmentAccountCode,
    string AdjustmentAccountName,
    string AdjustmentAccountRootType,
    bool IsActive,
    UserId? CreatedByUserId,
    UserId? UpdatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeactivatedAt);

public sealed record OpenItemAdjustmentAccountMappingLookupRequest(
    CompanyId CompanyId,
    string? OpenItemType,
    string? AdjustmentType,
    bool IncludeInactive,
    Guid? BookId,
    string? PolicyScope,
    string? SearchText,
    int Limit);

public sealed record OpenItemAdjustmentAccountMappingLookupSummary(
    int TotalMappings,
    int VisibleMappings,
    int ReturnedMappings,
    int ActiveMappings,
    int CompanyDefaultMappings,
    int BookSpecificMappings,
    int InactiveMappings);

public sealed record OpenItemAdjustmentAccountMappingLookupResult(
    OpenItemAdjustmentAccountMappingLookupSummary Summary,
    IReadOnlyList<OpenItemAdjustmentAccountMappingRecord> Mappings);

public sealed record OpenItemAdjustmentAccountMappingSaveRequest(
    CompanyId CompanyId,
    Guid? BookId,
    string OpenItemType,
    string AdjustmentType,
    Guid AdjustmentAccountId,
    UserId? ActorId);

public sealed record OpenItemAdjustmentAccountMappingSaveResult(
    OpenItemAdjustmentAccountMappingRecord Mapping,
    string OutcomeCode,
    string Message);

public sealed record OpenItemAdjustmentAccountMappingTransitionResult(
    OpenItemAdjustmentAccountMappingRecord Mapping,
    string TransitionCode,
    string OutcomeCode,
    string Message);

public interface IArOpenItemRepository
{
    Task EnsureForInvoiceAsync(
        InvoiceDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken);

    Task EnsureForCreditNoteAsync(
        CreditNoteDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken);

    Task<OpenItemDrillDown?> GetDrillDownAsync(
        CompanyId companyId,
        Guid openItemId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentPreview?> GetAdjustmentPreviewAsync(
        CompanyId companyId,
        Guid openItemId,
        string adjustmentType,
        DateOnly adjustmentDate,
        decimal? adjustmentAmountTx,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestAttempt?> RequestAdjustmentAsync(
        CompanyId companyId,
        Guid openItemId,
        string adjustmentType,
        DateOnly adjustmentDate,
        decimal? adjustmentAmountTx,
        UserId? actorId,
        string? reason,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestRecord?> GetLatestAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestTransitionResult?> SubmitAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestTransitionResult?> CancelAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestTransitionResult?> ApproveAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestTransitionResult?> RejectAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestReadiness?> GetAdjustmentRequestReadinessAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentExecutionPlan?> GetAdjustmentRequestExecutionPlanAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentExecutionPreparation?> PrepareAdjustmentExecutionAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        Guid adjustmentAccountId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentExecutionResult?> CompleteAdjustmentExecutionAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        Guid journalEntryId,
        string journalEntryDisplayNumber,
        DateTimeOffset executedAt,
        CancellationToken cancellationToken);
}

public interface IApOpenItemRepository
{
    Task EnsureForBillAsync(
        BillDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken);

    Task EnsureForVendorCreditAsync(
        VendorCreditDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken);

    Task<OpenItemDrillDown?> GetDrillDownAsync(
        CompanyId companyId,
        Guid openItemId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentPreview?> GetAdjustmentPreviewAsync(
        CompanyId companyId,
        Guid openItemId,
        string adjustmentType,
        DateOnly adjustmentDate,
        decimal? adjustmentAmountTx,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestAttempt?> RequestAdjustmentAsync(
        CompanyId companyId,
        Guid openItemId,
        string adjustmentType,
        DateOnly adjustmentDate,
        decimal? adjustmentAmountTx,
        UserId? actorId,
        string? reason,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestRecord?> GetLatestAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestTransitionResult?> SubmitAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestTransitionResult?> CancelAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestTransitionResult?> ApproveAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestTransitionResult?> RejectAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentRequestReadiness?> GetAdjustmentRequestReadinessAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentExecutionPlan?> GetAdjustmentRequestExecutionPlanAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentExecutionPreparation?> PrepareAdjustmentExecutionAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        Guid adjustmentAccountId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<OpenItemAdjustmentExecutionResult?> CompleteAdjustmentExecutionAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        Guid journalEntryId,
        string journalEntryDisplayNumber,
        DateTimeOffset executedAt,
        CancellationToken cancellationToken);
}

public sealed record OpenItemAdjustmentPreview(
    Guid OpenItemId,
    string OpenItemType,
    CompanyId CompanyId,
    string PartyRole,
    Guid PartyId,
    string SourceType,
    Guid SourceDocumentId,
    string SourceDocumentDisplayNumber,
    string SourceDocumentStatus,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    string BalanceSide,
    string Status,
    decimal OpenAmountTx,
    decimal OpenAmountBase,
    decimal RequestedAdjustmentAmountTx,
    decimal RequestedAdjustmentAmountBase,
    decimal RemainingAmountTx,
    decimal RemainingAmountBase,
    int ApplicationCount,
    string AdjustmentType,
    DateOnly AdjustmentDate,
    bool RequiresApproval,
    string ApprovalStatus,
    string ApprovalReason,
    string ActionCode,
    string ActionLabel,
    string AvailabilityMode,
    bool IsAvailable,
    string ExecutionMode,
    string Reason);

public sealed record OpenItemAdjustmentRequestRecord(
    Guid RequestId,
    Guid OpenItemId,
    string OpenItemType,
    CompanyId CompanyId,
    string AdjustmentType,
    DateOnly AdjustmentDate,
    decimal RequestedAdjustmentAmountTx,
    decimal RequestedAdjustmentAmountBase,
    bool RequiresApproval,
    string ApprovalStatus,
    string? ApprovedByActorType,
    UserId? ApprovedByActorId,
    DateTimeOffset? ApprovedAt,
    string? RejectedByActorType,
    UserId? RejectedByActorId,
    DateTimeOffset? RejectedAt,
    string RequestStatus,
    string ExecutionStatus,
    string RequestedByActorType,
    UserId? RequestedByActorId,
    DateTimeOffset RequestedAt,
    string? SubmittedByActorType,
    UserId? SubmittedByActorId,
    DateTimeOffset? SubmittedAt,
    string? CancelledByActorType,
    UserId? CancelledByActorId,
    DateTimeOffset? CancelledAt,
    string? Reason);

public sealed record OpenItemAdjustmentRequestAttempt(
    OpenItemAdjustmentPreview Preview,
    bool CommandAccepted,
    bool Executed,
    bool Persisted,
    string OutcomeCode,
    string Message,
    OpenItemAdjustmentRequestRecord? Request);

public sealed record OpenItemAdjustmentRequestTransitionResult(
    OpenItemAdjustmentRequestRecord Request,
    string TransitionCode,
    string OutcomeCode,
    string Message);

public sealed record OpenItemAdjustmentRequestReadiness(
    OpenItemAdjustmentRequestRecord Request,
    DateOnly AsOfDate,
    bool GovernanceReady,
    bool OpenItemReady,
    bool PostingExecutionReady,
    string ExecutionMode,
    string AvailabilityMode,
    bool IsAvailable,
    string Reason);

public sealed record OpenItemAdjustmentExecutionPlan(
    OpenItemAdjustmentRequestRecord Request,
    DateOnly AsOfDate,
    string ExecutionMode,
    bool CanExecute,
    string OverallStatus,
    string Reason,
    IReadOnlyList<OpenItemAdjustmentExecutionPlanStep> Steps);

public sealed record OpenItemAdjustmentExecutionPlanStep(
    int StepNumber,
    string StepCode,
    string StepLabel,
    string StepStatus,
    string Reason);

public sealed record OpenItemAdjustmentExecutionPreparation(
    OpenItemAdjustmentRequestReadiness Readiness,
    OpenItemAdjustmentDocument Document,
    Guid AdjustmentAccountId,
    decimal AdjustmentAmountTx,
    decimal AdjustmentAmountBase);

public sealed record OpenItemAdjustmentExecutionResult(
    OpenItemAdjustmentRequestRecord Request,
    DateOnly AsOfDate,
    string ExecutionMode,
    bool CommandAccepted,
    bool Executed,
    bool Persisted,
    string OutcomeCode,
    string Message,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    string? CompensationSourceType,
    decimal AdjustmentAmountTx,
    decimal AdjustmentAmountBase);

public interface ISettlementApplicationRepository
{
    Task ApplyReceivePaymentAsync(
        ReceivePaymentDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken);

    Task ApplyCreditApplicationAsync(
        CreditApplicationDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken);

    Task ApplyPayBillAsync(
        PayBillDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken);

    Task ApplyVendorCreditApplicationAsync(
        VendorCreditApplicationDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OpenItemApplicationDrillDown>> ListApplicationsAsync(
        CompanyId companyId,
        string targetOpenItemType,
        Guid targetOpenItemId,
        CancellationToken cancellationToken);
}

public interface IFxRevaluationApplyRepository
{
    Task ApplyAsync(
        FxRevaluationDocument document,
        UserId appliedByUserId,
        CancellationToken cancellationToken);
}

// ========================================================================
// Sales Receipt — cash-in-hand sale (no AR open item).
//
// The contract mirrors IInvoiceDocumentRepository's shape so the
// PostSalesReceiptCommandHandler can compose with the same posting
// engine plumbing the Invoice flow uses. SaveDraftAsync persists a
// draft row + lines (status='draft'); the journal entry writer
// flips status to 'posted' as part of PostingEngine.PostAsync once
// the matching source-type case lands in PostgresJournalEntryWriter.
//
// No SubmitDraftAsync — sales receipts always save-and-post in one
// step (cash already in hand). The /save-and-post endpoint chains
// SaveDraftAsync → PostSalesReceiptCommandHandler.HandleAsync.
// ========================================================================
public interface ISalesReceiptDocumentRepository
{
    Task<SalesReceiptDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesReceiptListItem>> ListAsync(
        CompanyId companyId,
        bool includeDrafts,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        SalesReceiptDraftSaveModel draft,
        CancellationToken cancellationToken);

    /// <summary>
    /// H6-2b: per-line task back-link lookup. Mirror of
    /// <see cref="IInvoiceDocumentRepository.ListLinkedTaskLineMappingsAsync"/>.
    /// The post handler uses the returned rows to drive line-level
    /// Task billing (sourceType = "sales_receipt"). Rows missing
    /// TaskLineId fall back to header-level marking through the
    /// existing whole-task path.
    /// </summary>
    Task<IReadOnlyList<SalesReceiptLineTaskLink>> ListLinkedTaskLineMappingsAsync(
        CompanyId companyId,
        Guid salesReceiptId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Sales-receipt analog of <see cref="InvoiceLineTaskLink"/>.
/// Same shape; kept as a distinct type so a future schema divergence
/// between the two doesn't force one row layout on the other.
/// </summary>
public sealed record class SalesReceiptLineTaskLink(
    Guid SalesReceiptLineId,
    Guid TaskId,
    Guid? TaskLineId);

public sealed record SalesReceiptListItem(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly ReceiptDate,
    Guid CustomerId,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    string PaymentMethod,
    DateTimeOffset? PostedAt,
    string? CustomerPoNumber = null);

public sealed record SalesReceiptDraftSaveModel(
    Guid? DocumentId,
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    Guid DepositToAccountId,
    string PaymentMethod,
    string? ReferenceNo,
    DateOnly ReceiptDate,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    Guid? FxSnapshotId,
    decimal? FxRate,
    DateOnly? FxEffectiveDate,
    string? FxSource,
    string? Memo,
    IReadOnlyList<SalesReceiptDraftLineSaveModel> Lines,
    string? CustomerPoNumber = null);

public sealed record SalesReceiptDraftLineSaveModel(
    int LineNumber,
    Guid RevenueAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    decimal TaxAmount,
    Guid? ItemId = null,
    // H6-2b: optional Task back-link. When non-null the line persists
    // into sales_receipt_lines.task_id (column added by the H6-1
    // migration). Pairs with TaskLineId below to drive the new
    // line-level billing path; TaskId alone falls back to the legacy
    // whole-task marking (same dual behavior as invoices in H6-2a).
    Guid? TaskId = null,
    // H6-2b: pins to a specific task_lines row. Same semantics as
    // InvoiceDraftLineSaveModel.TaskLineId.
    Guid? TaskLineId = null);

// ========================================================================
// Refund Receipt — cash-out customer refund.
//
// Mirror of ISalesReceiptDocumentRepository with the polarity flipped:
// SalesReceipt's DepositToAccount becomes RefundFromAccount, and the
// fragment builder credits (rather than debits) it. The Reason column
// captures why the refund was issued (return / pricing dispute / RMA #)
// because refund_receipts is one of the few documents where the GL
// fragments themselves don't tell the operator the "why".
// ========================================================================
public interface IRefundReceiptDocumentRepository
{
    Task<RefundReceiptDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RefundReceiptListItem>> ListAsync(
        CompanyId companyId,
        bool includeDrafts,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        RefundReceiptDraftSaveModel draft,
        CancellationToken cancellationToken);

    /// <summary>
    /// H6-3: per-line task back-link lookup. Refund Receipt is the
    /// reverse of Sales Receipt; when refund lines carry task_id /
    /// task_line_id, the post handler releases the corresponding
    /// task_lines (mirror of the credit-note path for AR invoices).
    /// </summary>
    Task<IReadOnlyList<RefundReceiptLineTaskLink>> ListLinkedTaskLineMappingsAsync(
        CompanyId companyId,
        Guid refundReceiptId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Refund-receipt analog of <see cref="CreditNoteLineTaskLink"/>.
/// Same shape, distinct type so a future divergence doesn't force
/// one row layout on the other.
/// </summary>
public sealed record class RefundReceiptLineTaskLink(
    Guid RefundReceiptLineId,
    Guid TaskId,
    Guid? TaskLineId);

public sealed record RefundReceiptListItem(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly RefundDate,
    Guid CustomerId,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    string PaymentMethod,
    DateTimeOffset? PostedAt,
    string? CustomerPoNumber = null);

public sealed record RefundReceiptDraftSaveModel(
    Guid? DocumentId,
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    Guid RefundFromAccountId,
    string PaymentMethod,
    string? ReferenceNo,
    string? Reason,
    DateOnly RefundDate,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    Guid? FxSnapshotId,
    decimal? FxRate,
    DateOnly? FxEffectiveDate,
    string? FxSource,
    string? Memo,
    IReadOnlyList<RefundReceiptDraftLineSaveModel> Lines,
    string? CustomerPoNumber = null);

public sealed record RefundReceiptDraftLineSaveModel(
    int LineNumber,
    Guid RevenueAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    decimal TaxAmount,
    Guid? ItemId = null,
    // H6-3 (D8): optional Task back-link. Persists into
    // refund_receipt_lines.task_id (column added by the H6-1
    // migration). Mirror of CreditNote's task back-link for AR
    // invoices — refund receipts reverse sales receipts.
    Guid? TaskId = null,
    Guid? TaskLineId = null);

// ========================================================================
// Bank Transfer — single-line internal account move.
// No "lines" table; the document IS the transfer.
// ========================================================================
public interface IBankTransferDocumentRepository
{
    Task<BankTransferDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BankTransferListItem>> ListAsync(
        CompanyId companyId,
        bool includeDrafts,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        BankTransferDraftSaveModel draft,
        CancellationToken cancellationToken);
}

public sealed record BankTransferListItem(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly TransferDate,
    Guid FromAccountId,
    string FromCurrencyCode,
    Guid ToAccountId,
    string ToCurrencyCode,
    decimal Amount,
    decimal? FxRate,
    DateTimeOffset? PostedAt);

public sealed record BankTransferDraftSaveModel(
    Guid? DocumentId,
    CompanyId CompanyId,
    UserId UserId,
    DateOnly TransferDate,
    Guid FromAccountId,
    string FromCurrencyCode,
    Guid ToAccountId,
    string ToCurrencyCode,
    decimal Amount,
    decimal? FxRate,
    Guid? FxSnapshotId,
    DateOnly? FxEffectiveDate,
    string? FxSource,
    string? ReferenceNo,
    string? Memo);

// ========================================================================
// Bank Deposit — multi-item deposit slip.
// Posting fragments: Dr DepositToAccount, Cr UndepositedFundsAccount
// (both for the total of the items). The undeposited-funds account
// is resolved by the repository at GetForPostingAsync time from
// company_settings (not stored on the deposit row itself).
// ========================================================================
public interface IBankDepositDocumentRepository
{
    Task<BankDepositDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BankDepositListItem>> ListAsync(
        CompanyId companyId,
        bool includeDrafts,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        BankDepositDraftSaveModel draft,
        CancellationToken cancellationToken);
}

public sealed record BankDepositListItem(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DepositDate,
    Guid DepositToAccountId,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    int ItemCount,
    DateTimeOffset? PostedAt);

public sealed record BankDepositDraftSaveModel(
    Guid? DocumentId,
    CompanyId CompanyId,
    UserId UserId,
    DateOnly DepositDate,
    Guid DepositToAccountId,
    string TransactionCurrencyCode,
    string? ReferenceNo,
    string? Memo,
    IReadOnlyList<BankDepositItemDraftSaveModel> Items);

public sealed record BankDepositItemDraftSaveModel(
    int LineNumber,
    string SourceItemKind,
    Guid? SourceItemId,
    string SourceItemDisplayNumber,
    string? PayerName,
    string? PaymentMethod,
    string? ReferenceNo,
    decimal Amount);

// ========================================================================
// Tax Return — single-row period close. No lines, no party.
// ========================================================================
public interface ITaxReturnDocumentRepository
{
    Task<TaxReturnDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TaxReturnListItem>> ListAsync(
        CompanyId companyId,
        bool includeDrafts,
        CancellationToken cancellationToken);

    Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        TaxReturnDraftSaveModel draft,
        CancellationToken cancellationToken);
}

public sealed record TaxReturnListItem(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    string TaxRegime,
    string FilingFrequency,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal NetAmount,
    string BaseCurrencyCode,
    DateTimeOffset? PostedAt);

public sealed record TaxReturnDraftSaveModel(
    Guid? DocumentId,
    CompanyId CompanyId,
    UserId UserId,
    string TaxRegime,
    string FilingFrequency,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string BaseCurrencyCode,
    decimal CollectedAmount,
    decimal InputCreditsAmount,
    decimal AdjustmentsAmount,
    string? AdjustmentsNote,
    string? RegulatorReferenceNo,
    string? Memo);
