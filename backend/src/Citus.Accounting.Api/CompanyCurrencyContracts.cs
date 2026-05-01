using SharedKernel.Company;

namespace Citus.Accounting.Api;

public sealed record EnableCompanyCurrencyHttpRequest(
    string CurrencyCode);

public sealed record CustomerUpsertHttpRequest(
    string DisplayName,
    string DefaultCurrencyCode,
    string? Email,
    string? Phone,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    string? TaxId,
    string? Notes,
    Guid? PaymentTermId);

public sealed record CustomerShippingAddressBookHttpRequest(
    string? Label,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    bool IsDefault);

public sealed record VendorShippingAddressBookHttpRequest(
    string? Label,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    bool IsDefault);

public sealed record VendorUpsertHttpRequest(
    string DisplayName,
    string DefaultCurrencyCode,
    string? Email,
    string? Phone,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    string? TaxId,
    string? Notes,
    Guid? PaymentTermId);

public sealed record PaymentTermUpsertHttpRequest(
    string? Code,
    string? Name,
    int? NetDays,
    bool? IsActive);

public sealed record QuoteUpsertHttpRequest(
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
    string? TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    decimal? ShippingAmount,
    Guid? ShippingTaxCodeId,
    string? MemoToCustomer,
    string? InternalNote,
    IReadOnlyList<QuoteLineHttpRequest>? Lines);

public sealed record QuoteLineHttpRequest(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    string? AccountCode);

public sealed record QuoteStatusHttpRequest(string? Status);

public sealed record SalesOrderUpsertHttpRequest(
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
    string? TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    decimal? ShippingAmount,
    Guid? ShippingTaxCodeId,
    string? MemoToCustomer,
    string? InternalNote,
    Guid? SourceQuoteId,
    IReadOnlyList<SalesOrderLineHttpRequest>? Lines);

public sealed record SalesOrderLineHttpRequest(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    string? AccountCode);

public sealed record SalesOrderStatusHttpRequest(string? Status);

public sealed record SalesOrderInvoicedHttpRequest(string? InvoiceNumber);

internal static class CompanyCurrencyResponseMapper
{
    public static object MapCurrencyProfile(CompanyCurrencyProfile profile) => new
    {
        profile.CompanyId,
        profile.LegalName,
        profile.BaseCurrencyCode,
        profile.MultiCurrencyEnabled,
        Currencies = profile.Currencies.Select(static currency => new
        {
            currency.CurrencyCode,
            currency.CurrencyName,
            currency.IsBaseCurrency,
            currency.IsEnabled
        }).ToArray()
    };
}
