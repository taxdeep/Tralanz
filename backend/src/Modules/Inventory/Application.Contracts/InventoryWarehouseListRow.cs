namespace Citus.Modules.Inventory.Application.Contracts;

/// <summary>
/// Warehouse-list projection used by the Tralanz Inventory →
/// Warehouses management page. V1 inventory tier is single-warehouse,
/// so this list almost always returns one row ("Main Warehouse"); the
/// shape is multi-row-ready so the ERP tier multi-warehouse work
/// doesn't need a contract change.
/// </summary>
public sealed record class InventoryWarehouseListRow(
    Guid Id,
    CompanyId CompanyId,
    string WarehouseCode,
    string Name,
    string? Description,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
