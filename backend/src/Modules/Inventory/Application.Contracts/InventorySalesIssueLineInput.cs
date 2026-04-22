namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventorySalesIssueLineInput(
    int LineNo,
    Guid ItemId,
    Guid WarehouseId,
    string UomCode,
    decimal Quantity,
    string? ReasonCode,
    string? Memo);
