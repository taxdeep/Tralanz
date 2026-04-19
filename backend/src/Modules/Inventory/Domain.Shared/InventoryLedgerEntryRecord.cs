namespace Citus.Modules.Inventory.Domain.Shared;

public sealed record class InventoryLedgerEntryRecord(
    Guid Id,
    Guid CompanyId,
    Guid ItemId,
    Guid WarehouseId,
    Guid? DocumentId,
    Guid? DocumentLineId,
    InventoryMovementDirection MovementDirection,
    InventoryMovementType MovementType,
    decimal QuantityDelta,
    decimal QuantityAfter,
    decimal CostAmountDeltaBase,
    decimal CostAmountAfterBase,
    decimal ReservedDelta,
    decimal ReservedAfter,
    decimal InTransitOutDelta,
    decimal InTransitOutAfter,
    decimal InTransitInDelta,
    decimal InTransitInAfter,
    string Message,
    Guid? CreatedByUserId,
    DateTimeOffset CreatedAt);
