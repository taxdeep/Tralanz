namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Per-company sales orders. The middle document in the sales pipeline:
/// upstream from Quote (created via convert), downstream of Invoice
/// (the SO is "completed" once an invoice is issued). No GL impact at
/// any stage — accounting hits the books only when the invoice posts.
///
/// Status lifecycle (M5 iter 1):
///
///   Open ──Confirm──▶ Confirmed ──Mark Invoiced──▶ Invoiced (terminal)
///     │                  │           ▲
///     │                  │           │
///     │                  └──Cancel──┘
///     │                              │
///     └──Mark Invoiced──────────────┘   (legacy direct path, no reservations)
///     │
///     └──Cancel──▶ Cancelled (terminal)
///
/// "Confirm" is the M5 entry point that activates inventory reservations:
/// per line, the requested qty is split into <see cref="SalesOrderLineRecord.ReservedQty"/>
/// (= min(qty, available)) and <see cref="SalesOrderLineRecord.BackorderQty"/> (= remainder),
/// and <c>item_warehouse_balances.reserved_qty</c> bumps to match.
/// SOs created without confirmation can still be invoiced directly
/// (legacy V0 behaviour) — they just skip the reservation lane.
/// </summary>
public interface ISalesOrderStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesOrderSummary>> ListAsync(
        CompanyId companyId,
        SalesOrderListFilter filter,
        CancellationToken cancellationToken);

    Task<SalesOrderRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken);

    Task<SalesOrderRecord> CreateAsync(
        CompanyId companyId,
        SalesOrderUpsertInput input,
        CancellationToken cancellationToken);

    Task<SalesOrderRecord?> UpdateAsync(
        CompanyId companyId,
        Guid salesOrderId,
        SalesOrderUpsertInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks the SO as Invoiced and stamps the user-supplied invoice
    /// number. V1 does not write into the existing invoices table —
    /// that flow stays in the Invoice module. This endpoint is the
    /// honest signal that "this SO has been billed elsewhere".
    /// </summary>
    Task<SalesOrderRecord?> MarkInvoicedAsync(
        CompanyId companyId,
        Guid salesOrderId,
        string invoiceNumber,
        CancellationToken cancellationToken);

    Task<SalesOrderRecord?> SetStatusAsync(
        CompanyId companyId,
        Guid salesOrderId,
        string newStatus,
        CancellationToken cancellationToken);

    /// <summary>
    /// M5 iter 1: confirms the SO and reserves on-hand stock per line.
    /// For each Stock-kind line, splits the requested quantity into
    /// `reserved_qty` (= min(qty, available)) and `backorder_qty` (= rest),
    /// then bumps <c>item_warehouse_balances.reserved_qty</c> by the
    /// reserved total. Service / NonStock lines are skipped (no inventory
    /// impact). Items configured with backorder_mode='disallow' fail the
    /// confirm with a clear shortage message rather than silently creating
    /// a backorder. Status flips Open → Confirmed; <c>confirmed_at</c>
    /// stamps the FIFO timestamp used by future M5 iter 2 receipt-side
    /// auto-promotion.
    /// </summary>
    Task<SalesOrderRecord?> ConfirmAsync(
        CompanyId companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// M5 iter 5: cancellation orchestrator. Releases any reservations
    /// the SO holds (decrements <c>item_warehouse_balances.reserved_qty</c>
    /// by each line's <c>reserved_qty</c>, zeroes the per-line
    /// reserved/backorder counters), flips status to 'cancelled', and
    /// returns the updated SO alongside a summary of any open customer
    /// deposits still pointed at this SO. Open deposits are NOT
    /// auto-refunded in V1 — the operator handles them via the existing
    /// Refund Receipt flow (or applies them to another SO). The summary
    /// in <see cref="SalesOrderCancelResult.OpenDepositSummary"/> tells
    /// the UI what to flag in the cancellation toast.
    ///
    /// Only Open and Confirmed SOs can cancel. Invoiced / Cancelled SOs
    /// stay terminal — re-issuing a cancellation throws
    /// InvalidOperationException with a precise message.
    /// </summary>
    Task<SalesOrderCancelResult?> CancelAsync(
        CompanyId companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Returned by <see cref="ISalesOrderStore.CancelAsync"/>. Carries the
/// updated SO record plus a deposit summary the UI uses to warn the
/// operator about leftover liability.
/// </summary>
public sealed record SalesOrderCancelResult(
    SalesOrderRecord SalesOrder,
    SalesOrderCancelDepositSummary OpenDepositSummary);

public sealed record SalesOrderCancelDepositSummary(
    int OpenDepositCount,
    decimal TotalOpenAmountBase);

public sealed record SalesOrderListFilter(
    string? Status,
    Guid? CustomerId,
    DateOnly? FromDate,
    DateOnly? ToDate);

public sealed record SalesOrderSummary(
    Guid Id,
    CompanyId CompanyId,
    string SalesOrderNumber,
    Guid CustomerId,
    string CustomerName,
    DateOnly DocumentDate,
    string Status,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    Guid? SourceQuoteId,
    string? InvoiceNumber,
    string? CustomerPoNumber,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SalesOrderRecord(
    Guid Id,
    CompanyId CompanyId,
    string SalesOrderNumber,
    string Status,
    Guid CustomerId,
    string CustomerName,
    string? CustomerNumber,
    DateOnly DocumentDate,
    string TransactionCurrencyCode,
    decimal? FxRate,
    string? BillingAddressLine,
    string? BillingCity,
    string? BillingProvinceState,
    string? BillingPostalCode,
    string? BillingCountry,
    string? ShippingAddressLine,
    string? ShippingCity,
    string? ShippingProvinceState,
    string? ShippingPostalCode,
    string? ShippingCountry,
    string? ShipVia,
    DateOnly? ShippingDate,
    string? TrackingNo,
    string TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    decimal? ShippingAmount,
    Guid? ShippingTaxCodeId,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? MemoToCustomer,
    string? InternalNote,
    Guid? SourceQuoteId,
    string? SourceQuoteNumber,
    string? InvoiceNumber,
    string? CustomerPoNumber,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SalesOrderLineRecord> Lines);

public sealed record SalesOrderLineRecord(
    Guid Id,
    Guid SalesOrderId,
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    Guid? TaxCodeSetId,
    string? AccountCode,
    decimal LineTotal,
    decimal ReservedQty,
    decimal BackorderQty,
    decimal ShippedQty);

public sealed record SalesOrderUpsertInput(
    Guid CustomerId,
    DateOnly DocumentDate,
    string TransactionCurrencyCode,
    decimal? FxRate,
    string? BillingAddressLine,
    string? BillingCity,
    string? BillingProvinceState,
    string? BillingPostalCode,
    string? BillingCountry,
    string? ShippingAddressLine,
    string? ShippingCity,
    string? ShippingProvinceState,
    string? ShippingPostalCode,
    string? ShippingCountry,
    string? ShipVia,
    DateOnly? ShippingDate,
    string? TrackingNo,
    string TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    decimal? ShippingAmount,
    Guid? ShippingTaxCodeId,
    string? MemoToCustomer,
    string? InternalNote,
    Guid? SourceQuoteId,
    string? CustomerPoNumber,
    IReadOnlyList<SalesOrderLineInput> Lines,
    // Optimistic-concurrency token; same contract as the bill / PO /
    // quote variants. Null preserves the legacy opt-out behaviour.
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record SalesOrderLineInput(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    Guid? TaxCodeSetId,
    string? AccountCode);

public static class SalesOrderStatus
{
    public const string Open = "open";
    /// <summary>
    /// SO has been confirmed: reservations have been bumped on
    /// item_warehouse_balances per Stock line; backorder lines are
    /// awaiting M5 iter 2's receipt-side auto-promotion. Confirmed SOs
    /// can still be cancelled (releases reservations) or marked
    /// invoiced.
    /// </summary>
    public const string Confirmed = "confirmed";
    public const string Invoiced = "invoiced";
    public const string Cancelled = "cancelled";

    public static bool IsValid(string? status) =>
        status is Open or Confirmed or Invoiced or Cancelled;

    public static bool IsTerminal(string status) => status is Invoiced or Cancelled;

    /// <summary>
    /// Open SOs are line-editable. Confirmed SOs are NOT — reservations
    /// have been bumped, so editing a line would desynchronise the
    /// reserved_qty trail. To edit a confirmed SO, cancel + re-create.
    /// </summary>
    public static bool IsEditable(string status) => status is Open;

    /// <summary>True for statuses where confirmation (reserve stock) is allowed.</summary>
    public static bool CanConfirm(string status) => status is Open;
}
