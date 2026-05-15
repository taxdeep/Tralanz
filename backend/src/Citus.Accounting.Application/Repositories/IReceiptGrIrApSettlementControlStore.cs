using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Repositories;

public interface IReceiptGrIrApSettlementControlStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<ReceiptGrIrApSettlementSummary> RefreshReceiptSettlementControlAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptGrIrApSettlementSummary> RefreshReceiptSettlementJournalReconciliationAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptGrIrApSettlementSummary> RefreshReceiptSettlementVarianceControlAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptGrIrApSettlementSummary?> GetReceiptSettlementSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ReceiptGrIrApSettlementSummary>> GetReceiptSettlementSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReceiptGrIrApSettlementBatchSummary>> ListReceiptSettlementBatchesAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReceiptGrIrApPurchaseVarianceLineSummary>> ListReceiptPurchaseVarianceLinesAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<BillGrIrApSettlementSummary?> GetBillSettlementSummaryAsync(
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptGrIrApSettlementExecutionResult> ExecuteReceiptSettlementAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        ReceiptGrIrApSettlementExecutionRequest request,
        CancellationToken cancellationToken);

    Task<ReceiptGrIrApOpenItemClearingResult> ClearReceiptSettlementOpenItemsAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken);

    Task<ReceiptGrIrApOpenItemClearingReversalResult> ReverseReceiptSettlementOpenItemClearingAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken);
}

public sealed record ReceiptGrIrApSettlementSummary(
    Guid ReceiptDocumentId,
    string SettlementStatus,
    int SettlementLineCount,
    int EligibleLineCount,
    int BlockedLineCount,
    int BlockedGrIrNotPostedLineCount,
    int BlockedBillNotPostedLineCount,
    int BlockedMissingApOpenItemLineCount,
    int BlockedJournalNotPostedLineCount,
    int BlockedAmountExceededLineCount,
    int PartiallySettledLineCount,
    int SettledLineCount,
    decimal SettlementAmountBase,
    decimal EligibleAmountBase,
    decimal SettledAmountBase,
    decimal RemainingAmountBase,
    int SettlementBatchCount,
    int JournalNotPostedBatchCount,
    int JournalPostedBatchCount,
    int JournalStaleBatchCount,
    int JournalInconsistentBatchCount,
    string JournalReconciliationStatus,
    DateTimeOffset? LastJournalRefreshedAt,
    int OpenItemNotClearedBatchCount,
    int OpenItemClearedBatchCount,
    int OpenItemReversedBatchCount,
    int OpenItemBlockedBatchCount,
    int OpenItemStaleBatchCount,
    int OpenItemInconsistentBatchCount,
    string OpenItemClearingStatus,
    DateTimeOffset? LastOpenItemClearedAt,
    DateTimeOffset? LastOpenItemReversedAt,
    int PurchaseVarianceLineCount,
    int PurchaseVarianceCandidateLineCount,
    int PurchaseVarianceNoVarianceLineCount,
    int PurchaseVarianceBlockedLineCount,
    string PurchaseVarianceStatus,
    decimal PurchaseVarianceAmountBase,
    DateTimeOffset? LastPurchaseVarianceRefreshedAt,
    DateTimeOffset? LastRefreshedAt,
    DateTimeOffset? LastSettledAt);

public sealed record ReceiptGrIrApSettlementBatchSummary(
    Guid ReceiptDocumentId,
    Guid SettlementBatchId,
    string Status,
    decimal RequestedAmountBase,
    decimal SettledQuantity,
    decimal SettledAmountBase,
    int LineCount,
    string JournalStatus,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    DateTimeOffset? JournalPostedAt,
    string? JournalBlockedReasonCode,
    string OpenItemClearingStatus,
    string? OpenItemClearingBlockedReasonCode,
    DateTimeOffset? OpenItemClearedAt,
    DateTimeOffset? OpenItemReversedAt,
    int OpenItemReversedApplicationCount,
    decimal OpenItemReversedAmountTx,
    decimal OpenItemReversedAmountBase,
    DateTimeOffset CreatedAt);

public sealed record ReceiptGrIrApPurchaseVarianceLineSummary(
    Guid ReceiptDocumentId,
    int ReceiptLineNumber,
    Guid SettlementBatchId,
    Guid SettlementBatchLineId,
    Guid BillDocumentId,
    int BillLineNumber,
    Guid ItemId,
    Guid WarehouseId,
    string UomCode,
    decimal SettledQuantity,
    decimal GrIrAmountBase,
    decimal BillAmountBase,
    decimal VarianceAmountBase,
    string VarianceStatus,
    string? BlockedReasonCode,
    DateTimeOffset RefreshedAt);

public sealed record BillGrIrApSettlementSummary(
    Guid BillDocumentId,
    string SettlementStatus,
    int SettlementLineCount,
    int EligibleLineCount,
    int BlockedLineCount,
    int BlockedGrIrNotPostedLineCount,
    int BlockedBillNotPostedLineCount,
    int BlockedMissingApOpenItemLineCount,
    int BlockedJournalNotPostedLineCount,
    int BlockedAmountExceededLineCount,
    int PartiallySettledLineCount,
    int SettledLineCount,
    decimal SettlementAmountBase,
    decimal EligibleAmountBase,
    decimal SettledAmountBase,
    decimal RemainingAmountBase,
    int SettlementBatchCount,
    int JournalNotPostedBatchCount,
    int JournalPostedBatchCount,
    int JournalStaleBatchCount,
    int JournalInconsistentBatchCount,
    string JournalReconciliationStatus,
    DateTimeOffset? LastJournalRefreshedAt,
    int OpenItemNotClearedBatchCount,
    int OpenItemClearedBatchCount,
    int OpenItemReversedBatchCount,
    int OpenItemBlockedBatchCount,
    int OpenItemStaleBatchCount,
    int OpenItemInconsistentBatchCount,
    string OpenItemClearingStatus,
    DateTimeOffset? LastOpenItemClearedAt,
    DateTimeOffset? LastOpenItemReversedAt,
    int PurchaseVarianceLineCount,
    int PurchaseVarianceCandidateLineCount,
    int PurchaseVarianceNoVarianceLineCount,
    int PurchaseVarianceBlockedLineCount,
    string PurchaseVarianceStatus,
    decimal PurchaseVarianceAmountBase,
    DateTimeOffset? LastPurchaseVarianceRefreshedAt,
    DateTimeOffset? LastRefreshedAt,
    DateTimeOffset? LastSettledAt);

public sealed record ReceiptGrIrApSettlementExecutionRequest(
    decimal? SettlementAmountBase,
    string? IdempotencyKey);

public sealed record ReceiptGrIrApSettlementExecutionResult(
    Guid ReceiptDocumentId,
    Guid SettlementBatchId,
    string IdempotencyKey,
    decimal RequestedAmountBase,
    decimal SettledQuantity,
    decimal SettledAmountBase,
    int SettlementLineCount,
    ReceiptGrIrApSettlementSummary Summary);

public sealed record ReceiptGrIrApOpenItemClearingResult(
    Guid ReceiptDocumentId,
    Guid SettlementBatchId,
    string ClearingStatus,
    int ApplicationCount,
    decimal ClearedAmountTx,
    decimal ClearedAmountBase,
    ReceiptGrIrApSettlementSummary Summary);

public sealed record ReceiptGrIrApOpenItemClearingReversalResult(
    Guid ReceiptDocumentId,
    Guid SettlementBatchId,
    string ClearingStatus,
    int ReversedApplicationCount,
    decimal RestoredAmountTx,
    decimal RestoredAmountBase,
    ReceiptGrIrApSettlementSummary Summary);

public static class ReceiptGrIrApSettlementStatusPolicy
{
    public const string NotEligible = "not_eligible";
    public const string EligibleNotSettled = "eligible_not_settled";
    public const string PartiallySettled = "partially_settled";
    public const string Settled = "settled";
    public const string Blocked = "blocked";
    public const string BlockedGrIrNotPosted = "blocked_grir_not_posted";
    public const string BlockedBillNotPosted = "blocked_bill_not_posted";
    public const string BlockedMissingApOpenItem = "blocked_missing_ap_open_item";
    public const string BlockedJournalNotPosted = "blocked_journal_not_posted";
    public const string BlockedAmountExceeded = "blocked_amount_exceeded";

    public static string ResolveSummaryStatus(
        int settlementLineCount,
        int eligibleLineCount,
        int blockedLineCount,
        int partiallySettledLineCount,
        int settledLineCount)
    {
        if (settlementLineCount <= 0)
        {
            return NotEligible;
        }

        if (blockedLineCount > 0)
        {
            return Blocked;
        }

        if (settledLineCount == settlementLineCount)
        {
            return Settled;
        }

        if (partiallySettledLineCount > 0 || settledLineCount > 0)
        {
            return PartiallySettled;
        }

        return eligibleLineCount > 0 ? EligibleNotSettled : NotEligible;
    }
}

public static class ReceiptGrIrApOpenItemClearingStatusPolicy
{
    public const string NotApplicable = "not_applicable";
    public const string NotCleared = "not_cleared";
    public const string Cleared = "cleared";
    public const string Reversed = "reversed";
    public const string PartiallyCleared = "partially_cleared";
    public const string PartiallyReversed = "partially_reversed";
    public const string Blocked = "blocked";
    public const string BlockedJournalNotPosted = "blocked_journal_not_posted";
    public const string BlockedJournalStale = "blocked_journal_stale";
    public const string BlockedJournalInconsistent = "blocked_journal_inconsistent";
    public const string BlockedMissingApOpenItem = "blocked_missing_ap_open_item";
    public const string BlockedOpenItemNotOpen = "blocked_ap_open_item_not_open";
    public const string BlockedAmountExceeded = "blocked_ap_open_item_amount_exceeded";
    public const string BlockedFxNotSupported = "blocked_fx_not_supported";
    public const string ClearingStale = "clearing_stale";
    public const string ClearingInconsistent = "clearing_inconsistent";

    public static string ResolveSummaryStatus(
        int settlementBatchCount,
        int openItemNotClearedBatchCount,
        int openItemClearedBatchCount,
        int openItemReversedBatchCount,
        int openItemBlockedBatchCount,
        int openItemStaleBatchCount,
        int openItemInconsistentBatchCount)
    {
        if (settlementBatchCount <= 0)
        {
            return NotApplicable;
        }

        if (openItemInconsistentBatchCount > 0)
        {
            return ClearingInconsistent;
        }

        if (openItemStaleBatchCount > 0)
        {
            return ClearingStale;
        }

        if (openItemBlockedBatchCount > 0)
        {
            return Blocked;
        }

        if (openItemClearedBatchCount == settlementBatchCount)
        {
            return Cleared;
        }

        if (openItemReversedBatchCount == settlementBatchCount)
        {
            return Reversed;
        }

        if (openItemClearedBatchCount > 0)
        {
            return PartiallyCleared;
        }

        if (openItemReversedBatchCount > 0)
        {
            return PartiallyReversed;
        }

        return openItemNotClearedBatchCount > 0 ? NotCleared : NotApplicable;
    }
}

public static class ReceiptGrIrApPurchaseVarianceStatusPolicy
{
    public const string NotApplicable = "not_applicable";
    public const string NoVariance = "no_variance";

    /// <summary>
    /// A non-zero variance is present and has been booked into the GR/IR
    /// settlement journal as a Dr/Cr Purchase Price Variance fragment
    /// (M4). Pre-M4 this was <c>candidate_not_reviewed</c> — the variance
    /// existed but no journal had captured it yet, so the operator was
    /// expected to review and post it manually. Post-M4 the variance is
    /// recognised as part of the settlement journal itself; the workbench
    /// surfaces these lines as "variance recognised, GL up to date" rather
    /// than "needs action".
    /// </summary>
    public const string RecognizedInSettlement = "recognized_in_settlement";

    public const string Blocked = "blocked";
    public const string BlockedSettlementNotPosted = "blocked_settlement_not_posted";
    public const string BlockedJournalNotPosted = "blocked_journal_not_posted";
    public const string BlockedOpenItemNotCleared = "blocked_open_item_not_cleared";
    public const string BlockedBillNotPosted = "blocked_bill_not_posted";
    public const string BlockedQuantityBasisMissing = "blocked_quantity_basis_missing";
    public const string VarianceInconsistent = "variance_inconsistent";

    public static string ResolveSummaryStatus(
        int varianceLineCount,
        int recognizedLineCount,
        int noVarianceLineCount,
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

        if (recognizedLineCount > 0)
        {
            return RecognizedInSettlement;
        }

        return noVarianceLineCount == varianceLineCount ? NoVariance : NotApplicable;
    }
}

public static class ReceiptGrIrApSettlementJournalStatusPolicy
{
    public const string NotApplicable = "not_applicable";
    public const string NotPosted = "not_posted";
    public const string Posted = "posted";
    public const string PartiallyPosted = "partially_posted";
    public const string JournalStale = "journal_stale";
    public const string JournalInconsistent = "journal_inconsistent";

    public static string ResolveSummaryStatus(
        int settlementBatchCount,
        int journalNotPostedBatchCount,
        int journalPostedBatchCount,
        int journalStaleBatchCount,
        int journalInconsistentBatchCount)
    {
        if (settlementBatchCount <= 0)
        {
            return NotApplicable;
        }

        if (journalInconsistentBatchCount > 0)
        {
            return JournalInconsistent;
        }

        if (journalStaleBatchCount > 0)
        {
            return JournalStale;
        }

        if (journalPostedBatchCount == settlementBatchCount)
        {
            return Posted;
        }

        if (journalPostedBatchCount > 0 || journalNotPostedBatchCount > 0)
        {
            return journalPostedBatchCount > 0
                ? PartiallyPosted
                : NotPosted;
        }

        return NotApplicable;
    }
}
