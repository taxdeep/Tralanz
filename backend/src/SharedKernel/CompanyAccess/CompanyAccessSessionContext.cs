namespace SharedKernel.CompanyAccess;

public sealed record class CompanyAccessSessionContext
{
    public CompanyAccessUserSummary User { get; init; } = new();

    public CompanyAccessCompanySummary ActiveCompany { get; init; } = new();

    public IReadOnlyList<CompanyAccessCompanySummary> AvailableCompanies { get; init; } = Array.Empty<CompanyAccessCompanySummary>();
}
