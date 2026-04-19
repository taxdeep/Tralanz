namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryReturnReceivePostRequest(
    Guid CompanyId,
    Guid UserId,
    Guid CustomerId,
    DateOnly PostingDate,
    Guid ShipmentDocumentId,
    string ShipmentDocumentNumber,
    string? Memo,
    IReadOnlyList<InventoryReturnReceiveLineInput> Lines);
