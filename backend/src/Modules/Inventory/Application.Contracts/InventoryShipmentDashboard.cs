namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryShipmentDashboard(
    Guid CompanyId,
    string BaseCurrencyCode,
    IReadOnlyList<InventoryManagedItemSummary> ActiveItems,
    IReadOnlyList<InventoryManagedWarehouseSummary> ActiveWarehouses,
    IReadOnlyList<InventoryShipmentSummary> RecentShipments);
