namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryBillReceiptPostingGateSnapshot(
    Guid BillDocumentId,
    int BillInboundLineCount,
    decimal BillInboundQuantity,
    int ReceiptCount,
    decimal ReceivedQuantity,
    decimal RemainingQuantity,
    string MatchStatus,
    DateTimeOffset? LatestReceiptPostedAt);
