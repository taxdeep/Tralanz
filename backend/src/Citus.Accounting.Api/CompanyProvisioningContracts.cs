namespace Citus.Accounting.Api;

/// <summary>
/// Body of <c>POST /accounting/companies</c> — the Business shell's
/// "+ New Company" wizard. The signed-in user becomes the new
/// company's owner; their <c>UserId</c> comes from the BusinessSession,
/// not from the request, so it can't be spoofed.
/// </summary>
public sealed record CreateAdditionalCompanyHttpRequest(
    string CompanyName,
    string EntityType,
    string Industry,
    DateTime IncorporatedOn,
    // MM-DD format, validated server-side.
    string FiscalYearEnd,
    string Country,
    string BaseCurrencyCode,
    int? AccountCodeLength,
    string? BusinessNumber,
    string? Phone,
    string? CompanyEmail,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? TemplateKey);
