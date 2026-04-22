namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryManufacturingDashboard(
    Guid CompanyId,
    string BaseCurrencyCode,
    IReadOnlyList<InventoryManagedItemSummary> ActiveItems,
    IReadOnlyList<InventoryManagedWarehouseSummary> ActiveWarehouses,
    IReadOnlyList<InventoryBomSummary> Boms,
    IReadOnlyList<InventoryManufacturingSummary> RecentRuns);
