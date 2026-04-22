namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryInvoiceShipmentHandoffLineSummary(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    string UomCode,
    int InvoiceLineCount,
    decimal InvoiceQuantity,
    decimal ShippedQuantity,
    decimal RemainingQuantity,
    string MatchStatus);
