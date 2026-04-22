namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryBillReceiptHandoffSummary(
    Guid BillDocumentId,
    int BillInboundLineCount,
    decimal BillInboundQuantity,
    int ReceiptCount,
    decimal ReceivedQuantity,
    decimal RemainingQuantity,
    string MatchStatus,
    DateTimeOffset? LatestReceiptPostedAt,
    IReadOnlyList<InventoryPurchaseReceiptSummary> RecentReceipts,
    IReadOnlyList<InventoryBillReceiptHandoffLineSummary> LineSummaries);
