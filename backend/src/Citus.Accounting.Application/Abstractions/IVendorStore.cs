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
        Guid companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<VendorRecord> CreateAsync(
        Guid companyId,
        VendorUpsertRequest request,
        CancellationToken cancellationToken);
}

public sealed record VendorRecord(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
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
    string? Notes);
