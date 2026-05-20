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
    IReadOnlyList<InventoryPurchaseReceiptLineInput> Lines)
{
    /// <summary>
    /// Client-supplied idempotency token sourced from the
    /// <c>Idempotency-Key</c> HTTP header. When non-empty, a retried
    /// request with the same key on the same company replays the
    /// existing document (via
    /// <see cref="InventoryIdempotencyReplayException"/>) instead of
    /// re-running the receipt. When null/empty, the post runs without
    /// the idempotency guarantee — legacy callers stay backwards
    /// compatible until they start sending the header.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}
