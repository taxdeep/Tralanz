using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Repositories;

public interface IReceiptGrIrApSettlementControlStore
{
    Task<ReceiptGrIrApSettlementSummary> RefreshReceiptSettlementControlAsync(
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
    DateTimeOffset? LastRefreshedAt,
    DateTimeOffset? LastSettledAt);

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
