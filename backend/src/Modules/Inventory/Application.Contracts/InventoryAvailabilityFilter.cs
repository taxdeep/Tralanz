namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryAvailabilityFilter(
    Guid? ItemId,
    Guid? WarehouseId);
