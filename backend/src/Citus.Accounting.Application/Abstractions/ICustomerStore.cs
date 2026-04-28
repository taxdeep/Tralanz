namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Per-company customer master data. Backs the Customers page and
/// supplies the AR-side counterparty source for invoices, receive
/// payments, credit notes, and AR aging. EntityNumber is generated
/// server-side to satisfy the customers_entity_number_format_chk
/// regex and is unique across the database (the wizard's
/// platform-wide entity-number contract).
/// </summary>
public interface ICustomerStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerRecord>> ListAsync(
        Guid companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<CustomerRecord> CreateAsync(
        Guid companyId,
        CustomerUpsertRequest request,
        CancellationToken cancellationToken);
}

public sealed record CustomerRecord(
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

public sealed record CustomerUpsertRequest(
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
