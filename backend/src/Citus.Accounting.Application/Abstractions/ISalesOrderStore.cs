namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Per-company sales orders. The middle document in the sales pipeline:
/// upstream from Quote (created via convert), downstream of Invoice
/// (the SO is "completed" once an invoice is issued). No GL impact at
/// any stage — accounting hits the books only when the invoice posts.
///
/// Status lifecycle:
///
///   Open ──Mark Invoiced──▶ Invoiced ──(terminal)
///       │                  ▲
///       │                  │
///       └──Cancel──▶ Cancelled
///
/// SO does not have a "Draft" stage — it is born from a Quote
/// conversion (or a manual create) and is immediately Open. Open SOs
/// remain editable; Invoiced and Cancelled are terminal.
/// </summary>
public interface ISalesOrderStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesOrderSummary>> ListAsync(
        Guid companyId,
        SalesOrderListFilter filter,
        CancellationToken cancellationToken);

    Task<SalesOrderRecord?> GetByIdAsync(
        Guid companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken);

    Task<SalesOrderRecord> CreateAsync(
        Guid companyId,
        SalesOrderUpsertInput input,
        CancellationToken cancellationToken);

    Task<SalesOrderRecord?> UpdateAsync(
        Guid companyId,
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
        Guid companyId,
        Guid salesOrderId,
        string invoiceNumber,
        CancellationToken cancellationToken);

    Task<SalesOrderRecord?> SetStatusAsync(
        Guid companyId,
        Guid salesOrderId,
        string newStatus,
        CancellationToken cancellationToken);
}

public sealed record SalesOrderListFilter(
    string? Status,
    Guid? CustomerId,
    DateOnly? FromDate,
    DateOnly? ToDate);

public sealed record SalesOrderSummary(
    Guid Id,
    Guid CompanyId,
    string SalesOrderNumber,
    Guid CustomerId,
    string CustomerName,
    DateOnly DocumentDate,
    string Status,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    Guid? SourceQuoteId,
    string? InvoiceNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SalesOrderRecord(
    Guid Id,
    Guid CompanyId,
    string SalesOrderNumber,
    string Status,
    Guid CustomerId,
    string CustomerName,
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
    string? AccountCode,
    decimal LineTotal);

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
    IReadOnlyList<SalesOrderLineInput> Lines);

public sealed record SalesOrderLineInput(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    string? AccountCode);

public static class SalesOrderStatus
{
    public const string Open = "open";
    public const string Invoiced = "invoiced";
    public const string Cancelled = "cancelled";

    public static bool IsValid(string? status) => status is Open or Invoiced or Cancelled;

    public static bool IsTerminal(string status) => status is Invoiced or Cancelled;

    public static bool IsEditable(string status) => status is Open;
}
