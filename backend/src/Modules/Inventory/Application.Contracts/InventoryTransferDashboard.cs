namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryTransferDashboard(
    CompanyId CompanyId,
    string BaseCurrencyCode,
    IReadOnlyList<InventoryManagedItemSummary> ActiveItems,
    IReadOnlyList<InventoryManagedWarehouseSummary> ActiveWarehouses,
    IReadOnlyList<InventoryTransferSummary> RecentTransfers);
