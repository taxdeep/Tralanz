namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryBomSummary(
    Guid BomId,
    Guid CompanyId,
    string BomCode,
    Guid OutputItemId,
    string OutputItemCode,
    string OutputItemName,
    string OutputUomCode,
    decimal OutputQuantity,
    bool IsActive,
    DateTimeOffset UpdatedAt,
    InventoryBomCostRollupSummary CostRollup,
    IReadOnlyList<InventoryBomComponentInput> Components);
