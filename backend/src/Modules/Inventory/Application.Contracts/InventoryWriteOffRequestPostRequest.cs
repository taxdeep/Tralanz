namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryWriteOffRequestPostRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid WarehouseId,
    DateOnly PostingDate,
    string? Memo,
    IReadOnlyList<InventoryAdjustmentLineInput> Lines,
    Guid? ClientRequestId = null);
