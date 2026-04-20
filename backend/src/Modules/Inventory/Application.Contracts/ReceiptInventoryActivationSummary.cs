namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class ReceiptInventoryActivationSummary(
    Guid ReceiptDocumentId,
    string ReceiptStatus,
    string ActivationStatus,
    Guid? InventoryDocumentId,
    int ReceiptLineCount,
    int ActivatedLineCount,
    decimal TotalQuantity,
    decimal ActivatedQuantity,
    DateTimeOffset? ActivatedAt,
    string? LastFailureMessage = null,
    DateTimeOffset? LastFailureAt = null);
