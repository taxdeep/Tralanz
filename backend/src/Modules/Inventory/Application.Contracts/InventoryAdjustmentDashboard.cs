using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryAdjustmentDashboard(
    CompanyId CompanyId,
    string BaseCurrencyCode,
    InventoryCostingPolicyRecord? CostingPolicy,
    IReadOnlyList<InventoryManagedItemSummary> ActiveItems,
    IReadOnlyList<InventoryManagedWarehouseSummary> ActiveWarehouses,
    IReadOnlyList<InventoryAdjustmentSummary> RecentAdjustments);
