using Citus.Ui.Shared.Control;
using Citus.Ui.Shared.Icons;
using Citus.Ui.Shared.Navigation;
using Citus.Ui.Shared.Shell;
using Citus.SysAdmin.Blazor.Services;

namespace Citus.SysAdmin.Blazor.State;

public sealed class AppShellState
{
    private static readonly IReadOnlyList<NavSection> SetupNavigationSections =
    [
        new NavSection
        {
            // Pre-first-company section was titled "Setup" — we kept "First
            // Company" out of the menu after the SysAdmin login redirects
            // straight into the wizard when FirstCompanySetupRequired is true,
            // so this menu only needs the operator-facing Core entries.
            Title = "Core",
            Items =
            [
                new NavMenuItem { Title = "Overview", Href = "overview", Icon = IconName.LayoutDashboard },
                new NavMenuItem { Title = "Server Console", Href = "operations/server", Icon = IconName.DeviceDesktop },
                new NavMenuItem { Title = "Database Backup", Href = "operations/database", Icon = IconName.Database }
            ]
        },
        new NavSection
        {
            Title = "Platform Control",
            Items =
            [
                new NavMenuItem { Title = "Audit", Href = "audit", Icon = IconName.Report },
                new NavMenuItem { Title = "Security", Href = "security", Icon = IconName.ShieldLock },
                new NavMenuItem { Title = "Maintenance", Href = "maintenance", Icon = IconName.Tool },
                new NavMenuItem { Title = "Runtime Health", Href = "runtime-health", Icon = IconName.Activity }
            ]
        }
    ];

    private static readonly IReadOnlyList<NavSection> BusinessReadyNavigationSections =
    [
        new NavSection
        {
            Title = "Control",
            Items =
            [
                new NavMenuItem { Title = "Overview", Href = "overview", Icon = IconName.LayoutDashboard },
                new NavMenuItem { Title = "Modules", Href = "modules", Icon = IconName.Puzzle },
                new NavMenuItem { Title = "Entities", Href = "entities", Icon = IconName.Database }
            ]
        },
        new NavSection
        {
            Title = "Operations",
            Items =
            [
                new NavMenuItem { Title = "Companies", Href = "companies", Icon = IconName.BuildingSkyscraper },
                new NavMenuItem { Title = "Users", Href = "users", Icon = IconName.Users },
                new NavMenuItem { Title = "Audit", Href = "audit", Icon = IconName.Report },
                new NavMenuItem { Title = "Security", Href = "security", Icon = IconName.ShieldLock },
                new NavMenuItem { Title = "SMTP", Href = "operations/smtp", Icon = IconName.Tool },
                new NavMenuItem { Title = "AI Provider", Href = "operations/ai-provider", Icon = IconName.Puzzle },
                new NavMenuItem { Title = "AI Activity", Href = "operations/ai-activity", Icon = IconName.Activity },
                new NavMenuItem { Title = "Server Console", Href = "operations/server", Icon = IconName.DeviceDesktop },
                new NavMenuItem { Title = "Database", Href = "operations/database", Icon = IconName.Database },
                new NavMenuItem { Title = "Locked Accounts", Href = "operations/lockouts", Icon = IconName.ShieldLock },
                new NavMenuItem { Title = "Maintenance", Href = "maintenance", Icon = IconName.Tool },
                new NavMenuItem { Title = "Runtime Health", Href = "runtime-health", Icon = IconName.Activity }
            ]
        }
    ];

    public SysAdminAuthSessionSummary? AuthSession { get; private set; }

    public string SessionToken { get; private set; } = string.Empty;

    public bool IsAuthenticated => AuthSession is not null && !string.IsNullOrWhiteSpace(SessionToken);

    public bool HasLoadedSetupStatus { get; private set; }

    public string SetupStage { get; private set; } = "uninitialized";

    public int AccountCount { get; private set; }

    public int CompanyCount { get; private set; }

    public int OwnerMembershipCount { get; private set; }

    public bool HasAnyAccount { get; private set; }

    public bool HasAnyCompany { get; private set; }

    public bool HasAnyOwnerMembership { get; private set; }

    public bool SetupRequired { get; private set; } = true;

    public bool BusinessInitializationPending { get; private set; }

    public bool BusinessReady { get; private set; }

    public bool FirstCompanySetupRequired { get; private set; }

    public bool FirstCompanySetupDeferred { get; private set; }

    public DateTimeOffset? FirstCompanySetupDeferredAtUtc { get; private set; }

    public SysAdminAuthenticationClient.FirstCompanyProvisioningResponse? FirstCompanyCompletion { get; private set; }

    public bool HasFirstCompanyCompletion => FirstCompanyCompletion is not null;

    public SysAdminOperatorSummary Operator { get; private set; } = new()
    {
        DisplayName = "Platform Administrator",
        Email = "sysadmin@tralanz.local",
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

    public IReadOnlyList<NavSection> NavigationSections =>
        BusinessReady ? BusinessReadyNavigationSections : SetupNavigationSections;

    public void SetMaintenanceState(MaintenanceStateSummary state)
    {
        MaintenanceState = state;
    }

    public void SetActiveCompany(CompanyContextSummary company)
    {
        ActiveCompany = company;
    }

    public void ApplySetupStatus(SysAdminAuthenticationClient.SetupStatusResponse status)
    {
        HasLoadedSetupStatus = true;
        SetupStage = string.IsNullOrWhiteSpace(status.SetupStage) ? "uninitialized" : status.SetupStage.Trim().ToLowerInvariant();
        AccountCount = status.AccountCount;
        CompanyCount = status.CompanyCount;
        OwnerMembershipCount = status.OwnerMembershipCount;
        HasAnyAccount = status.HasAnyAccount;
        HasAnyCompany = status.HasAnyCompany;
        HasAnyOwnerMembership = status.HasAnyOwnerMembership;
        SetupRequired = status.SetupRequired;
        BusinessInitializationPending = status.BusinessInitializationPending;
        BusinessReady = status.BusinessReady;
        FirstCompanySetupRequired = status.FirstCompanySetupRequired;
        FirstCompanySetupDeferred = status.FirstCompanySetupDeferred;
        FirstCompanySetupDeferredAtUtc = status.FirstCompanySetupDeferredAtUtc;

        if (!BusinessReady)
        {
            ActiveCompany = new CompanyContextSummary
            {
                CompanyCode = "SYS",
                CompanyName = "Platform Control",
                IsSystemScope = true
            };
            AvailableCompanies = Array.Empty<CompanyWorkspaceSummary>();
        }
    }

    public void SetFirstCompanyCompletion(SysAdminAuthenticationClient.FirstCompanyProvisioningResponse completion)
    {
        FirstCompanyCompletion = completion;
    }

    public void ClearFirstCompanyCompletion()
    {
        FirstCompanyCompletion = null;
    }

    public void ApplyAuthenticatedSession(string sessionToken, SysAdminAuthSessionSummary session)
    {
        SessionToken = sessionToken.Trim();
        AuthSession = session;
        Operator = new SysAdminOperatorSummary
        {
            DisplayName = session.DisplayName,
            Email = session.Email,
            Roles = session.Roles
        };
    }

    public void ClearAuthenticatedSession()
    {
        AuthSession = null;
        SessionToken = string.Empty;
        HasLoadedSetupStatus = false;
        SetupStage = "uninitialized";
        AccountCount = 0;
        CompanyCount = 0;
        OwnerMembershipCount = 0;
        HasAnyAccount = false;
        HasAnyCompany = false;
        HasAnyOwnerMembership = false;
        SetupRequired = true;
        BusinessInitializationPending = false;
        BusinessReady = false;
        FirstCompanySetupRequired = false;
        FirstCompanySetupDeferred = false;
        FirstCompanySetupDeferredAtUtc = null;
        FirstCompanyCompletion = null;
        Operator = new SysAdminOperatorSummary
        {
            DisplayName = "Platform Administrator",
            Email = "sysadmin@tralanz.local",
            Roles = ["sysadmin"]
        };
        ActiveCompany = new CompanyContextSummary
        {
            CompanyCode = "SYS",
            CompanyName = "Platform Control",
            IsSystemScope = true
        };
        AvailableCompanies = Array.Empty<CompanyWorkspaceSummary>();
    }

    public void ApplyControlContext(SysAdminControlContextSummary context)
    {
        Operator = context.Operator;
        MaintenanceState = context.MaintenanceState;

        if (BusinessReady)
        {
            ActiveCompany = context.ActiveCompany;
            AvailableCompanies = context.AvailableCompanies;
            return;
        }

        ActiveCompany = new CompanyContextSummary
        {
            CompanyCode = "SYS",
            CompanyName = "Platform Control",
            IsSystemScope = true
        };
        AvailableCompanies = Array.Empty<CompanyWorkspaceSummary>();
    }
}
