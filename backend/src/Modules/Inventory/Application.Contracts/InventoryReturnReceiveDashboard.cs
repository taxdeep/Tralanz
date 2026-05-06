namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryReturnReceiveDashboard(
    CompanyId CompanyId,
    IReadOnlyList<InventoryShipmentSummary> RecentShipments,
    IReadOnlyList<InventoryReturnReceiveSummary> RecentReturns);
