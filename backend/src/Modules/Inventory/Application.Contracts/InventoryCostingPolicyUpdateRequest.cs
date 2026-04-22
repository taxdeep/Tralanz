using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryCostingPolicyUpdateRequest(
    Guid CompanyId,
    Guid UserId,
    InventoryCostingMethod DefaultCostingMethod,
    bool NegativeStockAllowed,
    bool RequireWriteOffApproval);
