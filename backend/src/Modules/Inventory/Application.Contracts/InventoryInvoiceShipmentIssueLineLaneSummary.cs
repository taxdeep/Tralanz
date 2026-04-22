namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryInvoiceShipmentIssueLineLaneSummary(
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
    decimal RemainingToShipQuantity,
    string ShipmentMatchStatus,
    decimal InvoicedQuantity,
    decimal RemainingToInvoiceQuantity,
    string InvoiceCoverageStatus,
    decimal IssuedQuantity,
    decimal RemainingToIssueQuantity,
    string IssueMatchStatus);
