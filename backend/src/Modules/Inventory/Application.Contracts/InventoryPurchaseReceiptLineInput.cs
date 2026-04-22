namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryPurchaseReceiptLineInput(
    int LineNo,
    Guid ItemId,
    Guid WarehouseId,
    string UomCode,
    decimal Quantity,
    decimal UnitCostTx,
    string? ReasonCode,
    string? Memo);
