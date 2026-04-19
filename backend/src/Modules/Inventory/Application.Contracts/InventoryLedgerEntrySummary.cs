namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryLedgerEntrySummary(
    Guid LedgerEntryId,
    DateOnly PostingDate,
    string MovementType,
    string MovementDirection,
    Guid ItemId,
    string ItemCode,
    string ItemName,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    string DocumentNumber,
    decimal QuantityDelta,
    decimal QuantityAfter,
    decimal CostAmountDeltaBase,
    decimal CostAmountAfterBase,
    string? Memo,
    DateTimeOffset CreatedAt);
