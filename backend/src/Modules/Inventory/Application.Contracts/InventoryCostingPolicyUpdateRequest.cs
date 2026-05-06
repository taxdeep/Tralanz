using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryCostingPolicyUpdateRequest(
    CompanyId CompanyId,
    UserId UserId,
    InventoryCostingMethod DefaultCostingMethod,
    bool NegativeStockAllowed,
    bool RequireWriteOffApproval);
