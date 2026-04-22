namespace Citus.Modules.Inventory.Domain.Shared;

public sealed record class InventoryCostingPolicyRecord(
    Guid CompanyId,
    InventoryCostingMethod DefaultCostingMethod,
    bool NegativeStockAllowed,
    bool RequireWriteOffApproval,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    Guid? UpdatedByUserId,
    DateTimeOffset UpdatedAt);
