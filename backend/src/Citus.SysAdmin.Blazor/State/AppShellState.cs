using Citus.Ui.Shared.Control;
using Citus.Ui.Shared.Navigation;
using Citus.Ui.Shared.Shell;
using MudBlazor;

namespace Citus.SysAdmin.Blazor.State;

public sealed class AppShellState
{
    public SysAdminOperatorSummary Operator { get; private set; } = new()
    {
        DisplayName = "Platform Administrator",
        Email = "sysadmin@citus.local",
        Roles = ["sysadmin"]
    };

    public CompanyContextSummary ActiveCompany { get; private set; } = new()
    {
        CompanyCode = "SYS",
        CompanyName = "Platform Control",
        IsSystemScope = true
    };

    public IReadOnlyList<CompanyWorkspaceSummary> AvailableCompanies { get; private set; } = Array.Empty<CompanyWorkspaceSummary>();

    public MaintenanceStateSummary MaintenanceState { get; private set; } = new()
    {
        Enabled = false,
        Message = "Platform runtime is accepting interactive changes."
    };

    public IReadOnlyList<NavSection> NavigationSections { get; } =
    [
        new NavSection
        {
            Title = "Control",
            Items =
            [
                new NavMenuItem { Title = "Overview", Href = "overview", Icon = Icons.Material.Filled.DashboardCustomize },
                new NavMenuItem { Title = "Modules", Href = "modules", Icon = Icons.Material.Filled.Extension },
                new NavMenuItem { Title = "Entities", Href = "entities", Icon = Icons.Material.Filled.Schema }
            ]
        },
        new NavSection
        {
            Title = "Operations",
            Items =
            [
                new NavMenuItem { Title = "Companies", Href = "companies", Icon = Icons.Material.Filled.Domain },
                new NavMenuItem { Title = "Users", Href = "users", Icon = Icons.Material.Filled.People },
                new NavMenuItem { Title = "Maintenance", Href = "maintenance", Icon = Icons.Material.Filled.BuildCircle },
                new NavMenuItem { Title = "Runtime Health", Href = "runtime-health", Icon = Icons.Material.Filled.MonitorHeart }
            ]
        }
    ];

    public void SetMaintenanceState(MaintenanceStateSummary state)
    {
        MaintenanceState = state;
    }

    public void SetActiveCompany(CompanyContextSummary company)
    {
        ActiveCompany = company;
    }

    public void ApplyControlContext(SysAdminControlContextSummary context)
    {
        Operator = context.Operator;
        ActiveCompany = context.ActiveCompany;
        MaintenanceState = context.MaintenanceState;
        AvailableCompanies = context.AvailableCompanies;
    }
}
