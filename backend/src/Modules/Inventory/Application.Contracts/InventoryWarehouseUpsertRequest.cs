namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryWarehouseUpsertRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? WarehouseId,
    string WarehouseCode,
    string Name,
    string? Description);
