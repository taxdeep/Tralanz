namespace Modules.AP.PurchaseOrders;

/// <summary>
/// Per-company purchase-order document surface for the Tralanz Books
/// AP page. Owns the <c>ap_purchase_orders</c> + <c>ap_purchase_order_lines</c>
/// tables — distinct from the inventory-grade <c>purchase_orders</c>
/// table that <see cref="Citus.Accounting.Infrastructure.Persistence.PostgresPurchaseOrderDocumentRepository"/>
/// owns. The two tables coexist in V1: this one is the user-facing AP
/// document surface (Category-mode lines, no qty/uom requirement), the
/// other is the inventory-grade PO domain (line.item_id NOT NULL,
/// uom_code, three-quantity truth tracking). Convergence between them
/// is a migration item for the Inventory batch.
///
/// Status lifecycle:
///
///   Draft  ──Send──▶ Open ──ConvertToBill / ConvertToExpense──▶ Closed
///       │             │
///       │             └──Cancel──▶ Cancelled
///       └──Void──▶ Void
///
/// V1 simplifications (per "framework first, inventory integration
/// later"):
///   - Lines are Category-only (ExpenseAccountId required, ItemId is
///     a nullable forward-compat hook).
///   - Convert to Bill / Expense is allowed multiple times. No partial
///     billing tracking — once at least one downstream document is
///     generated the PO transitions to Closed and stays there.
///   - GL impact: none. PO is informational only.
/// </summary>
public interface IPurchaseOrderStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PurchaseOrderSummary>> ListAsync(
        CompanyId companyId,
        PurchaseOrderListFilter filter,
        CancellationToken cancellationToken);

    Task<PurchaseOrderRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken);

    Task<PurchaseOrderRecord> CreateAsync(
        CompanyId companyId,
        PurchaseOrderUpsertInput input,
        CancellationToken cancellationToken);

    Task<PurchaseOrderRecord?> UpdateAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        PurchaseOrderUpsertInput input,
        CancellationToken cancellationToken);

    Task<PurchaseOrderRecord?> SetStatusAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        string newStatus,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stamps the PO as Closed when at least one downstream Bill /
    /// Expense has been generated. Idempotent — calling twice is a
    /// no-op when the status is already Closed.
    /// </summary>
    Task<PurchaseOrderRecord?> MarkClosedAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken);
}

public sealed record PurchaseOrderListFilter(
    bool IncludeDrafts,
    string? Status,
    Guid? VendorId,
    DateOnly? FromDate,
    DateOnly? ToDate);

public sealed record PurchaseOrderSummary(
    Guid Id,
    CompanyId CompanyId,
    string PurchaseOrderNumber,
    Guid VendorId,
    string VendorName,
    DateOnly OrderDate,
    DateOnly? ExpectedDeliveryDate,
    string Status,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PurchaseOrderRecord(
    Guid Id,
    CompanyId CompanyId,
    string PurchaseOrderNumber,
    string Status,
    Guid VendorId,
    string VendorName,
    DateOnly OrderDate,
    DateOnly? ExpectedDeliveryDate,
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
    string? MemoToSupplier,
    string? InternalNote,
    Guid? PaymentTermId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PurchaseOrderLineRecord> Lines);

public sealed record PurchaseOrderLineRecord(
    Guid Id,
    Guid PurchaseOrderId,
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid? ExpenseAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    decimal LineTotal);

public sealed record PurchaseOrderUpsertInput(
    Guid VendorId,
    DateOnly OrderDate,
    DateOnly? ExpectedDeliveryDate,
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
    string? MemoToSupplier,
    string? InternalNote,
    Guid? PaymentTermId,
    IReadOnlyList<PurchaseOrderLineInput> Lines,
    // Optimistic-concurrency token threaded down from the route layer.
    // When non-null, UpdateAsync's UPDATE narrows on updated_at; a
    // mismatch raises ConcurrencyConflictException so the route can
    // surface 409. Null = legacy opt-out preserved for old callers.
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record PurchaseOrderLineInput(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid? ExpenseAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId);

public static class PurchaseOrderStatus
{
    public const string Draft = "draft";
    public const string Open = "open";
    public const string Closed = "closed";
    public const string Cancelled = "cancelled";
    public const string Void = "void";

    public static bool IsValid(string? status) =>
        status is Draft or Open or Closed or Cancelled or Void;

    public static bool IsTerminal(string status) =>
        status is Closed or Cancelled or Void;

    public static bool IsEditable(string status) =>
        status is Draft or Open;

    /// <summary>True when the PO can spawn downstream Bill / Expense documents.</summary>
    public static bool CanConvert(string status) =>
        status is Open or Closed; // Closed POs allow additional conversions in V1 (multiple bills allowed).
}

public static class PurchaseOrderTaxMode
{
    public const string Exclusive = "exclusive";
    public const string Inclusive = "inclusive";

    public static bool IsValid(string? mode) => mode is Exclusive or Inclusive;
}
