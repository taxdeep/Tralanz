namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryTransferUpsertRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? TransferId,
    Guid SourceWarehouseId,
    Guid DestinationWarehouseId,
    string? Memo,
    IReadOnlyList<InventoryTransferLineInput> Lines);
