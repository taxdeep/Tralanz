namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryPurchaseReceiptPostRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    DateOnly PostingDate,
    string TransactionCurrencyCode,
    decimal FxRateToBase,
    string? SourceModule,
    Guid? SourceDocumentId,
    string? SourceDocumentNumber,
    string? Memo,
    IReadOnlyList<InventoryPurchaseReceiptLineInput> Lines);
