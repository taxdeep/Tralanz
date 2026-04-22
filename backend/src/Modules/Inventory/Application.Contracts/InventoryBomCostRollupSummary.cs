namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryBomCostRollupSummary(
    decimal EstimatedTotalCostBase,
    decimal EstimatedUnitCostBase,
    bool IsComplete,
    string? Note);
