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
    IReadOnlyList<ExpenseLineHttpRequest>? Lines);

public sealed record ExpenseLineHttpRequest(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid ExpenseAccountId,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    Guid? TaskId = null);
