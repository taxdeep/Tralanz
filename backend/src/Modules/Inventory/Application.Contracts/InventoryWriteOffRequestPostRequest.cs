namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryWriteOffRequestPostRequest(
    Guid CompanyId,
    Guid UserId,
    Guid WarehouseId,
    DateOnly PostingDate,
    string? Memo,
    IReadOnlyList<InventoryAdjustmentLineInput> Lines);
