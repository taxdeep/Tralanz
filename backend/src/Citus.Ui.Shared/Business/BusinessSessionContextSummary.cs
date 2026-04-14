using Citus.Ui.Shared.Shell;

namespace Citus.Ui.Shared.Business;

public sealed record class BusinessSessionContextSummary
{
    public BusinessUserSummary User { get; init; } = new();

    public BusinessCompanySummary ActiveCompany { get; init; } = new();

    public IReadOnlyList<BusinessCompanySummary> AvailableCompanies { get; init; } = Array.Empty<BusinessCompanySummary>();

    public MaintenanceStateSummary MaintenanceState { get; init; } = new();
}
