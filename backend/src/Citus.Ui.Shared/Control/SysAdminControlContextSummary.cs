using Citus.Ui.Shared.Shell;

namespace Citus.Ui.Shared.Control;

public sealed record class SysAdminControlContextSummary
{
    public SysAdminOperatorSummary Operator { get; init; } = new();

    public CompanyContextSummary ActiveCompany { get; init; } = new();

    public MaintenanceStateSummary MaintenanceState { get; init; } = new();

    public IReadOnlyList<CompanyWorkspaceSummary> AvailableCompanies { get; init; } = Array.Empty<CompanyWorkspaceSummary>();
}
