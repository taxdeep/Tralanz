namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryPurchaseReceiptDashboard(
    CompanyId CompanyId,
    string BaseCurrencyCode,
    IReadOnlyList<InventoryManagedItemSummary> ActiveItems,
    IReadOnlyList<InventoryManagedWarehouseSummary> ActiveWarehouses,
    IReadOnlyList<InventoryPurchaseReceiptSummary> RecentReceipts);
