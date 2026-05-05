using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryAdjustmentSummary(
    Guid DocumentId,
    CompanyId CompanyId,
    string DocumentNumber,
    string Status,
    InventoryAdjustmentKind AdjustmentKind,
    DateOnly PostingDate,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    decimal TotalQuantity,
    decimal TotalCostBase,
    int LineCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? PostedAt,
    string? Memo);
