namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryManufacturingSummary(
    Guid RunId,
    Guid CompanyId,
    string RunNumber,
    Guid BomId,
    string BomCode,
    Guid OutputItemId,
    string OutputItemCode,
    string OutputItemName,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    decimal OutputQuantity,
    decimal TotalConsumedCostBase,
    decimal UnitCostBase,
    string IssueDocumentNumber,
    string ReceiptDocumentNumber,
    DateTimeOffset PostedAt,
    string? Memo);
