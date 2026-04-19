namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryReturnReceiveLineInput(
    int LineNo,
    Guid ItemId,
    Guid WarehouseId,
    string UomCode,
    decimal Quantity,
    string ConditionCode,
    string ReturnReasonCode,
    string? DispositionReasonCode,
    string? Memo);
