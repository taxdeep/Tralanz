namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryOutboundDiscrepancySummary(
    string LaneType,
    Guid SourceDocumentId,
    Guid ItemId,
    string ItemCode,
    string ItemName,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    string UomCode,
    string Status,
    decimal SourceQuantity,
    decimal MatchedQuantity,
    decimal RemainingQuantity,
    DateTimeOffset? LatestMatchedAt,
    string Summary);
