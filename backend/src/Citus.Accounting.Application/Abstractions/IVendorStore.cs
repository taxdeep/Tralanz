namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Per-company vendor master data. AP-side mirror of
/// <see cref="ICustomerStore"/>: backs the Vendors page and supplies
/// the counterparty source for bills, pay-bill settlement, and AP
/// aging. EntityNumber is generated server-side to satisfy the
/// vendors_entity_number_format_chk regex.
/// </summary>
public interface IVendorStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<VendorRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<VendorRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken);

    Task<VendorRecord> CreateAsync(
        CompanyId companyId,
        VendorUpsertRequest request,
        CancellationToken cancellationToken);

    Task<VendorRecord?> UpdateAsync(
        CompanyId companyId,
        Guid vendorId,
        VendorUpsertRequest request,
        CancellationToken cancellationToken);
}

public sealed record VendorRecord(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    // Operator-facing vendor code (VEN-NNNNNN by default), drawn from
    // the vendor-display numbering scope. Null on rows created before
    // the scope was wired; the UI falls back to EntityNumber.
    string? VendorNumber,
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
    Guid? PaymentTermId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record VendorUpsertRequest(
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
