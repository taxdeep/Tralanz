using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryAdjustmentPostRequest(
    CompanyId CompanyId,
    UserId UserId,
    InventoryAdjustmentKind AdjustmentKind,
    Guid WarehouseId,
    DateOnly PostingDate,
    string? Memo,
    IReadOnlyList<InventoryAdjustmentLineInput> Lines,
    Guid? ClientRequestId = null);
