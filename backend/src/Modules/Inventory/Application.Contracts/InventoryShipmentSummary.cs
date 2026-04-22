namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryShipmentSummary(
    Guid DocumentId,
    Guid CompanyId,
    string DocumentNumber,
    string Status,
    DateOnly PostingDate,
    Guid CustomerId,
    string CustomerDisplayName,
    decimal TotalQuantity,
    int LineCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PostedAt,
    string? CarrierName,
    string? TrackingNumber,
    string? ShippingSlipNumber,
    string? Memo,
    decimal IssuedQuantity,
    decimal RemainingToIssueQuantity,
    string IssueMatchStatus,
    DateTimeOffset? LatestIssuePostedAt,
    IReadOnlyList<InventoryShipmentIssueLineSummary> IssueLineSummaries,
    IReadOnlyList<InventorySalesIssueSummary> RecentIssues,
    IReadOnlyList<InventoryShipmentLineInput> Lines);

public sealed record class InventoryShipmentIssueLineSummary(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    string UomCode,
    int ShipmentLineCount,
    decimal ShipmentQuantity,
    decimal IssuedQuantity,
    decimal RemainingToIssueQuantity,
    string MatchStatus);
