namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryWarehouseUpsertRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? WarehouseId,
    string WarehouseCode,
    string Name,
    string? Description);
