namespace Citus.Accounting.Application.Companies;

/// <summary>
/// Read-only lookup of the issuing company's profile for surfaces that
/// need to render the company on a document (invoice / quote / PO).
/// Backs the invoice PDF header in Batch 1; will back the email signature
/// and the company-bound template defaults in later batches.
/// </summary>
public interface ICompanyProfileQuery
{
    Task<CompanyProfileSnapshot?> GetByIdAsync(Guid companyId, CancellationToken cancellationToken);
}

public sealed record CompanyProfileSnapshot(
    Guid Id,
    string EntityNumber,
    string LegalName,
    string? Email,
    string? Phone,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    string BaseCurrencyCode);
