namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryTransferSummary(
    Guid TransferId,
    Guid CompanyId,
    string TransferNumber,
    string Status,
    Guid SourceWarehouseId,
    string SourceWarehouseCode,
    string SourceWarehouseName,
    Guid DestinationWarehouseId,
    string DestinationWarehouseCode,
    string DestinationWarehouseName,
    decimal TotalQuantity,
    int LineCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? ReceivedAt,
    string? Memo);
