namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryReturnReceiveHandoffLineSummary(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    string UomCode,
    decimal ShippedQuantity,
    decimal ReturnedQuantity,
    decimal RemainingReturnableQuantity,
    string MatchStatus);
