namespace Citus.Modules.Inventory.Domain.Shared;

public sealed record class InventoryCostingPolicyRecord(
    CompanyId CompanyId,
    InventoryCostingMethod DefaultCostingMethod,
    bool NegativeStockAllowed,
    bool RequireWriteOffApproval,
    UserId CreatedByUserId,
    DateTimeOffset CreatedAt,
    UserId? UpdatedByUserId,
    DateTimeOffset UpdatedAt);
