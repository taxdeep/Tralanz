namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryAvailabilityDashboard(
    CompanyId CompanyId,
    string BaseCurrencyCode,
    IReadOnlyList<InventoryManagedItemSummary> ActiveItems,
    IReadOnlyList<InventoryManagedWarehouseSummary> ActiveWarehouses,
    IReadOnlyList<InventoryItemAvailabilitySummary> AvailabilityRows,
    IReadOnlyList<InventoryLedgerEntrySummary> RecentLedgerEntries,
    InventoryAvailabilityLedgerDrillDown? DrillDown);
