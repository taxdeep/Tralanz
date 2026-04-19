namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryReturnReceiveHandoffSummary(
    Guid ShipmentDocumentId,
    string ShipmentDocumentNumber,
    Guid CustomerId,
    string CustomerDisplayName,
    DateOnly ShipmentPostingDate,
    int ShipmentLineCount,
    decimal ShippedQuantity,
    int ReturnReceiptCount,
    decimal ReturnedQuantity,
    decimal RemainingReturnableQuantity,
    string MatchStatus,
    DateTimeOffset? LatestReturnPostedAt,
    IReadOnlyList<InventoryReturnReceiveSummary> RecentReturns,
    IReadOnlyList<InventoryReturnReceiveHandoffLineSummary> LineSummaries);
