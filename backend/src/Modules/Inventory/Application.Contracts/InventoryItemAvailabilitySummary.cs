namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryItemAvailabilitySummary(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    decimal OnHandQty,
    decimal ReservedQty,
    decimal AvailableQty,
    decimal InTransitOutQty,
    decimal InTransitInQty,
    decimal CostBalanceBase,
    DateTimeOffset? LastMovementAt);
