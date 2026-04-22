namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryAdjustmentLineInput(
    int LineNo,
    Guid ItemId,
    string UomCode,
    decimal Quantity,
    decimal? UnitCostBase,
    string? ReasonCode,
    string? Memo);
