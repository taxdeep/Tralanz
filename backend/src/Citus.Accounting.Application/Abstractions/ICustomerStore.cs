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

    Task<CustomerRecord?> GetByIdAsync(
        Guid companyId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task<CustomerRecord> CreateAsync(
        Guid companyId,
        CustomerUpsertRequest request,
        CancellationToken cancellationToken);

    Task<CustomerRecord?> UpdateAsync(
        Guid companyId,
        Guid customerId,
        CustomerUpsertRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Distinct shipping addresses this customer has been quoted /
    /// sold to before, sorted by most-recent-use desc then by usage
    /// count desc. Source: union of `quotes` + `sales_orders` rows
    /// for this customer that have at least one shipping_* field
    /// set. Backs the AddressEditor drawer's "Use a previous
    /// shipping address" picker — pure unitySearch-style "use = learn"
    /// (no separate address-book table needed).
    /// </summary>
    Task<IReadOnlyList<CustomerShippingAddressRecord>> ListShippingAddressHistoryAsync(
        Guid companyId,
        Guid customerId,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record CustomerShippingAddressRecord(
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    int UsageCount,
    DateOnly LastUsedOn);

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
    Guid? PaymentTermId,
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
    string? Notes,
    Guid? PaymentTermId);
