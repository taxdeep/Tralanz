namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryTransferUpsertRequest(
    Guid CompanyId,
    Guid UserId,
    Guid? TransferId,
    Guid SourceWarehouseId,
    Guid DestinationWarehouseId,
    string? Memo,
    IReadOnlyList<InventoryTransferLineInput> Lines);
