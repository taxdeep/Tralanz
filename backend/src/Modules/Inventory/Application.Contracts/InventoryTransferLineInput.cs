namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryTransferLineInput(
    int LineNo,
    Guid ItemId,
    string UomCode,
    decimal Quantity,
    string? Memo);
