namespace Citus.Accounting.Api;

/// <summary>
/// HTTP request shapes for the Bill module. The Bill module lives in
/// <c>Modules.AP.Bills</c> (per the Tralanz brand-neutral namespace
/// rule); this file holds the API-edge wrappers that translate JSON
/// payloads into the module's <see cref="Modules.AP.Bills.BillUpsertInput"/>.
/// </summary>
public sealed record BillUpsertHttpRequest(
    string BillNumber,
    Guid VendorId,
    DateOnly BillDate,
    DateOnly DueDate,
    string DocumentCurrencyCode,
    decimal? FxRate,
    string? Memo,
    Guid? PaymentTermId,
    Guid? SourcePurchaseOrderId,
    string? SourcePurchaseOrderNumber,
    IReadOnlyList<BillLineHttpRequest>? Lines,
    // Optimistic-concurrency token. Round-trips the updated_at the
    // editor saw on GET; the store rejects the UPDATE with a 409 if
    // the value no longer matches. Null on first save / opt-out.
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record BillLineHttpRequest(
    int LineNumber,
    Guid ExpenseAccountId,
    string? Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal? TaxAmount);
