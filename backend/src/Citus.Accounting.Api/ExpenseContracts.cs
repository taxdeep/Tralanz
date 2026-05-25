namespace Citus.Accounting.Api;

/// <summary>
/// HTTP request shapes for the AP-side Expense surface
/// (<c>Modules.AP.Expenses</c>).
/// </summary>
public sealed record ExpenseUpsertHttpRequest(
    string PayeeKind,
    Guid? PayeeId,
    string? PayeeNameFreeform,
    Guid PaymentAccountId,
    string PaymentMethod,
    string? ChequeNumber,
    string? RefNo,
    string TransactionCurrencyCode,
    decimal? FxRate,
    DateOnly PaymentDate,
    Guid? SourcePurchaseOrderId,
    string? SourcePurchaseOrderNumber,
    string? TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    string? Memo,
    string? InternalNote,
    IReadOnlyList<ExpenseLineHttpRequest>? Lines,
    // Copy A3 Phase 1: when set, the client copied the form fields
    // from an existing expense and wants the server to audit the
    // provenance. Nullable so the field is optional in the wire format
    // (existing clients keep working). Not stored on the expense row
    // itself — recorded only in audit_logs.
    Guid? CopiedFromExpenseId = null);

public sealed record ExpenseLineHttpRequest(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid ExpenseAccountId,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    // Optional Task this line bills against. Validated server-side via
    // ITaskLineLinkValidator before insert; persists to expense_lines.task_id
    // (column added by Batch 8). Feeds the Batch 10 margin report.
    Guid? TaskId = null);
