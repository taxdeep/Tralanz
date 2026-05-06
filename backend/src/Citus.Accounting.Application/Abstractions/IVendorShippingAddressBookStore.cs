namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// First-class shipping address book per vendor — AP-side mirror of
/// <see cref="ICustomerShippingAddressBookStore"/>. Use cases on the
/// vendor side are narrower than on the customer side (returns,
/// drop-ship origins, alternate ship-to for vendor-managed inventory),
/// but the shape is identical so operators get a consistent experience
/// across counterparties.
///
/// Schema invariant: at most one row per (company_id, vendor_id) has
/// <c>is_default = true</c>; the unique partial index in the Postgres
/// impl enforces it. Insert / Update / SetDefault clear any previous
/// default in the same transaction.
/// </summary>
public interface IVendorShippingAddressBookStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<VendorShippingAddressBookEntry>> ListAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken);

    Task<VendorShippingAddressBookEntry?> GetAsync(
        CompanyId companyId,
        Guid vendorId,
        Guid addressId,
        CancellationToken cancellationToken);

    Task<VendorShippingAddressBookEntry> InsertAsync(
        CompanyId companyId,
        Guid vendorId,
        VendorShippingAddressBookUpsertRequest request,
        CancellationToken cancellationToken);

    Task<VendorShippingAddressBookEntry?> UpdateAsync(
        CompanyId companyId,
        Guid vendorId,
        Guid addressId,
        VendorShippingAddressBookUpsertRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        CompanyId companyId,
        Guid vendorId,
        Guid addressId,
        CancellationToken cancellationToken);

    Task<VendorShippingAddressBookEntry?> SetDefaultAsync(
        CompanyId companyId,
        Guid vendorId,
        Guid addressId,
        CancellationToken cancellationToken);
}

public sealed record VendorShippingAddressBookEntry(
    Guid Id,
    CompanyId CompanyId,
    Guid VendorId,
    string? Label,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record VendorShippingAddressBookUpsertRequest(
    string? Label,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    bool IsDefault);
