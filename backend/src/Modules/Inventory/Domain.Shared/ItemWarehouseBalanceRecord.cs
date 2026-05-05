namespace Citus.Modules.Inventory.Domain.Shared;

public sealed record class ItemWarehouseBalanceRecord(
    CompanyId CompanyId,
    Guid ItemId,
    Guid WarehouseId,
    decimal OnHandQty,
    decimal ReservedQty,
    decimal InTransitOutQty,
    decimal InTransitInQty,
    DateTimeOffset? LastMovementAt)
{
    public decimal AvailableQty => OnHandQty - ReservedQty;
}
