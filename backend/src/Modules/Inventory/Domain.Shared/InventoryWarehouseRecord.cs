namespace Citus.Modules.Inventory.Domain.Shared;

public sealed record class InventoryWarehouseRecord(
    Guid Id,
    Guid CompanyId,
    string WarehouseCode,
    string Name,
    string AddressLine,
    string City,
    string ProvinceState,
    string Country,
    string PostalCode,
    bool IsActive);
