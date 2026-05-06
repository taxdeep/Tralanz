namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryShipmentDashboard(
    CompanyId CompanyId,
    string BaseCurrencyCode,
    IReadOnlyList<InventoryManagedItemSummary> ActiveItems,
    IReadOnlyList<InventoryManagedWarehouseSummary> ActiveWarehouses,
    IReadOnlyList<InventoryShipmentSummary> RecentShipments);
