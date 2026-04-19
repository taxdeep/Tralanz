namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryReturnReceiveSummary(
    Guid DocumentId,
    Guid CompanyId,
    string DocumentNumber,
    string Status,
    DateOnly PostingDate,
    Guid CustomerId,
    string CustomerDisplayName,
    Guid ShipmentDocumentId,
    string ShipmentDocumentNumber,
    decimal TotalQuantity,
    int LineCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PostedAt,
    decimal ReturnedQuantity,
    decimal RemainingReturnableQuantity,
    string ReturnMatchStatus,
    string? Memo,
    IReadOnlyList<InventoryReturnReceiveLineInput> Lines);
