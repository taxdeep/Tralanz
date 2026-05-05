using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryFoundationEnsureRequest(
    CompanyId CompanyId,
    UserId UserId,
    InventoryCostingMethod DefaultCostingMethod = InventoryCostingMethod.MovingAverage,
    bool NegativeStockAllowed = false,
    bool RequireWriteOffApproval = true);
