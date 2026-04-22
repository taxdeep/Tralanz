namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryBomComponentInput(
    int LineNo,
    Guid ComponentItemId,
    decimal Quantity,
    decimal WastagePercent,
    string? Memo);
