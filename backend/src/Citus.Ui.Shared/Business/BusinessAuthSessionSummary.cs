namespace Citus.Ui.Shared.Business;

public sealed record class BusinessAuthSessionSummary
{
    public BusinessUserSummary User { get; init; } = new();

    public BusinessCompanySummary ActiveCompany { get; init; } = new();

    public IReadOnlyList<BusinessCompanySummary> AvailableCompanies { get; init; } = Array.Empty<BusinessCompanySummary>();
}
