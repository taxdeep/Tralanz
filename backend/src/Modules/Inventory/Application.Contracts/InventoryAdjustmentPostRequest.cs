using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryAdjustmentPostRequest(
    Guid CompanyId,
    Guid UserId,
    InventoryAdjustmentKind AdjustmentKind,
    Guid WarehouseId,
    DateOnly PostingDate,
    string? Memo,
    IReadOnlyList<InventoryAdjustmentLineInput> Lines);
