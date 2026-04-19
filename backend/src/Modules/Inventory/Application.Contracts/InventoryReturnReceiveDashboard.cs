namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryReturnReceiveDashboard(
    Guid CompanyId,
    IReadOnlyList<InventoryShipmentSummary> RecentShipments,
    IReadOnlyList<InventoryReturnReceiveSummary> RecentReturns);
