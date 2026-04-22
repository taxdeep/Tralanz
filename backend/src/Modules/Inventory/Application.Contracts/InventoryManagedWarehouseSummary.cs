namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryManagedWarehouseSummary(
    Guid Id,
    Guid CompanyId,
    string WarehouseCode,
    string Name,
    string? Description,
    bool IsActive,
    DateTimeOffset UpdatedAt);
