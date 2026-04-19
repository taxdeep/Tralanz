using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryFoundationSummary(
    Guid CompanyId,
    InventoryCostingPolicyRecord? CostingPolicy,
    int ItemCount,
    int WarehouseCount,
    int ActiveWarehouseCount,
    int BalanceCount,
    int LedgerEntryCount,
    int CostLayerCount);
