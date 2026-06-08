namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Per-company sales-side quotes (a.k.a. estimates). Pre-sales document
/// — informational only until accepted and converted into a Sales
/// Order. No GL impact at any stage. Status lifecycle:
///
///   Draft ──Send──▶ Pending ──Accept──▶ Accepted ──Convert──▶ Converted
///       │              │                    │
///       │              ├──Reject──▶ Rejected
///       │              └──Expire──▶ Expired
///       └──Void──▶ Void
///
/// Save in the UI implicitly creates a Draft if no quote id exists; an
/// explicit "Send" transitions Draft → Pending. Once Converted the
/// quote is immutable and links to its Sales Order via
/// <see cref="QuoteRecord.ConvertedSalesOrderId"/>.
/// </summary>
public interface IQuoteStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<QuoteSummary>> ListAsync(
        CompanyId companyId,
        QuoteListFilter filter,
        CancellationToken cancellationToken);

    Task<QuoteRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid quoteId,
        CancellationToken cancellationToken);

    Task<QuoteRecord> CreateAsync(
        CompanyId companyId,
        QuoteUpsertInput input,
        CancellationToken cancellationToken);

    Task<QuoteRecord?> UpdateAsync(
        CompanyId companyId,
        Guid quoteId,
        QuoteUpsertInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transitions the quote to <paramref name="newStatus"/>. Server
    /// validates the transition. Returns <c>null</c> when the quote is
    /// not found; throws <see cref="InvalidOperationException"/> when
    /// the transition is not allowed from the current state.
    /// </summary>
    Task<QuoteRecord?> SetStatusAsync(
        CompanyId companyId,
        Guid quoteId,
        string newStatus,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks the quote as converted and stamps the resulting sales-order
    /// id. Caller is responsible for creating the SO row first; this
    /// just closes the loop on the quote side.
    /// </summary>
    Task<QuoteRecord?> MarkConvertedAsync(
        CompanyId companyId,
        Guid quoteId,
        Guid salesOrderId,
        CancellationToken cancellationToken);
}

public sealed record QuoteListFilter(
    bool IncludeDrafts,
    string? Status,
    Guid? CustomerId,
    DateOnly? FromDate,
    DateOnly? ToDate);

public sealed record QuoteSummary(
    Guid Id,
    CompanyId CompanyId,
    string QuoteNumber,
    Guid CustomerId,
    string CustomerName,
    DateOnly DocumentDate,
    DateOnly? ExpirationDate,
    string Status,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    string? CustomerPoNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record QuoteRecord(
    Guid Id,
    CompanyId CompanyId,
    string QuoteNumber,
    string Status,
    Guid CustomerId,
    string CustomerName,
    DateOnly DocumentDate,
    DateOnly? ExpirationDate,
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
    Guid? ConvertedSalesOrderId,
    string? CustomerPoNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<QuoteLineRecord> Lines);

public sealed record QuoteLineRecord(
    Guid Id,
    Guid QuoteId,
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    Guid? TaxCodeSetId,
    string? AccountCode,
    decimal LineTotal);

public sealed record QuoteUpsertInput(
    Guid CustomerId,
    DateOnly DocumentDate,
    DateOnly? ExpirationDate,
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
    string? CustomerPoNumber,
    IReadOnlyList<QuoteLineInput> Lines,
    // Optimistic-concurrency token. Same shape as BillUpsertInput +
    // PurchaseOrderUpsertInput — null = opt-out, set = enforce
    // updated_at match in the store's UPDATE.
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record QuoteLineInput(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    Guid? TaxCodeSetId,
    string? AccountCode);

public static class QuoteStatus
{
    public const string Draft = "draft";
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Converted = "converted";
    public const string Void = "void";

    public static bool IsValid(string? status) =>
        status is Draft or Pending or Accepted or Rejected or Expired or Converted or Void;

    public static bool IsTerminal(string status) =>
        status is Rejected or Converted or Void;

    public static bool IsEditable(string status) =>
        status is Draft or Pending;
}

public static class QuoteTaxMode
{
    public const string Exclusive = "exclusive";
    public const string Inclusive = "inclusive";

    public static bool IsValid(string? mode) => mode is Exclusive or Inclusive;
}
