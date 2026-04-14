using Citus.Ui.Shared.Business;

namespace Citus.Accounting.Api;

public sealed record class BusinessSessionResolution
{
    public BusinessUserSummary User { get; init; } = new();

    public BusinessCompanySummary ActiveCompany { get; init; } = new();

    public IReadOnlyList<BusinessCompanySummary> AvailableCompanies { get; init; } = Array.Empty<BusinessCompanySummary>();
}
