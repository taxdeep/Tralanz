namespace Citus.Accounting.Application.Companies;

/// <summary>
/// Read-only lookup of the issuing company's profile for surfaces that
/// need to render the company on a document (invoice / quote / PO).
/// Backs the invoice PDF header in Batch 1; will back the email signature
/// and the company-bound template defaults in later batches.
/// </summary>
public interface ICompanyProfileQuery
{
    Task<CompanyProfileSnapshot?> GetByIdAsync(CompanyId companyId, CancellationToken cancellationToken);
}

public sealed record CompanyProfileSnapshot(
    CompanyId Id,
    string EntityNumber,
    string LegalName,
    string? Email,
    string? Phone,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    string BaseCurrencyCode,
    // Operator-chosen chart-of-accounts code length (4–10), set during
    // first-company provisioning. CoA seeders that target this company
    // scale canonical 5-digit template codes to this width so additive
    // seeds (e.g. Inventory module activation) don't drop a 5-digit
    // account into a chart whose other rows are 6/7 digits.
    int AccountCodeLength);
