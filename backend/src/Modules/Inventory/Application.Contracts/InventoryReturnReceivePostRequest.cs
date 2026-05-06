namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryReturnReceivePostRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    DateOnly PostingDate,
    Guid ShipmentDocumentId,
    string ShipmentDocumentNumber,
    string? Memo,
    IReadOnlyList<InventoryReturnReceiveLineInput> Lines);
