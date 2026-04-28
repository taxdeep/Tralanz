namespace Citus.Accounting.Api;

/// <summary>
/// HTTP request shapes for the AP-side Purchase Order surface
/// (<c>Modules.AP.PurchaseOrders</c>).
/// </summary>
public sealed record PurchaseOrderUpsertHttpRequest(
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
    string? TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    decimal? ShippingAmount,
    Guid? ShippingTaxCodeId,
    string? MemoToSupplier,
    string? InternalNote,
    Guid? PaymentTermId,
    IReadOnlyList<PurchaseOrderLineHttpRequest>? Lines);

public sealed record PurchaseOrderLineHttpRequest(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid? ExpenseAccountId,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId);

public sealed record PurchaseOrderStatusHttpRequest(string? Status);
