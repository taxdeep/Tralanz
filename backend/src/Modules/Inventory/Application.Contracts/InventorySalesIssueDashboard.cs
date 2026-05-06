namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventorySalesIssueDashboard(
    CompanyId CompanyId,
    string BaseCurrencyCode,
    IReadOnlyList<InventoryManagedItemSummary> ActiveItems,
    IReadOnlyList<InventoryManagedWarehouseSummary> ActiveWarehouses,
    IReadOnlyList<InventorySalesIssueSummary> RecentIssues);
