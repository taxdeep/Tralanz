namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// First-class shipping address book per customer. Distinct from the
/// historical address picker exposed by
/// <c>ICustomerStore.ListShippingAddressHistoryAsync</c>: that one is
/// derived read-only from past quotes / sales orders, this one is a
/// real CRUD surface so an operator can pre-register a warehouse / 3PL /
/// alternate ship-to without first cutting a document.
///
/// Both sources will eventually feed the AddressEditor's "Use a previous
/// address" picker. Wiring them together is a follow-up — this store
/// only owns the persisted entries.
/// </summary>
public interface ICustomerShippingAddressBookStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerShippingAddressBookEntry>> ListAsync(
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task<CustomerShippingAddressBookEntry?> GetAsync(
        CompanyId companyId,
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken);

    Task<CustomerShippingAddressBookEntry> InsertAsync(
        CompanyId companyId,
        Guid customerId,
        CustomerShippingAddressBookUpsertRequest request,
        CancellationToken cancellationToken);

    Task<CustomerShippingAddressBookEntry?> UpdateAsync(
        CompanyId companyId,
        Guid customerId,
        Guid addressId,
        CustomerShippingAddressBookUpsertRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        CompanyId companyId,
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks one entry as the customer's default shipping address. The
    /// store is responsible for clearing any previous default in the
    /// same transaction so the (company, customer, is_default=true)
    /// invariant holds at most one row.
    /// </summary>
    Task<CustomerShippingAddressBookEntry?> SetDefaultAsync(
        CompanyId companyId,
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken);
}

/// <summary>
/// One persisted entry in the customer's shipping address book. Label
/// is operator-supplied free text — typical values: "Warehouse A",
/// "3PL", "Receiving dock", "Main store". Empty label is allowed; the
/// UI falls back to the first non-empty address line in that case.
/// </summary>
public sealed record CustomerShippingAddressBookEntry(
    Guid Id,
    CompanyId CompanyId,
    Guid CustomerId,
    string? Label,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CustomerShippingAddressBookUpsertRequest(
    string? Label,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    bool IsDefault);
