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
    DateTimeOffset? ExpectedUpdatedAt = null,
    // Copy A3 Phase 2: when set, the bill was prefilled from another
    // bill via More → Copy. Recorded in audit_logs alongside the
    // regular CREATE; no FK on the bills row.
    Guid? CopiedFromBillId = null);

public sealed record BillLineHttpRequest(
    int LineNumber,
    Guid ExpenseAccountId,
    string? Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal? TaxAmount,
    // Optional Task this line bills against. When non-null the route
    // calls ITaskLineLinkValidator before writing so the link can never
    // settle on a billed / canceled / cross-company task. Persists into
    // bill_lines.task_id (column added by Batch 8) and feeds the Batch 10
    // margin-report direct-cost rollup.
    Guid? TaskId = null);
