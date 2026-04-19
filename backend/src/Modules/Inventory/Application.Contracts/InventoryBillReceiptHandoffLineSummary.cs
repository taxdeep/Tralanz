namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryBillReceiptHandoffLineSummary(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    string UomCode,
    int BillLineCount,
    decimal BillQuantity,
    decimal ReceivedQuantity,
    decimal RemainingQuantity,
    string MatchStatus);
