using Citus.Business.Blazor.Configuration;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Icons;
using Citus.Ui.Shared.Navigation;
using Citus.Ui.Shared.Shell;
using Microsoft.Extensions.Options;

namespace Citus.Business.Blazor.State;

public sealed class BusinessShellState
{
    public BusinessShellState(IOptions<AppHostOptions> options)
    {
        var bootstrap = options.Value;

        User = new BusinessUserSummary
        {
            Id = bootstrap.BootstrapUserId,
            DisplayName = bootstrap.BootstrapUserDisplayName,
            Email = bootstrap.BootstrapUserEmail,
            Username = bootstrap.BootstrapUsername,
            Roles = bootstrap.BootstrapRoles
        };

        ActiveCompany = new BusinessCompanySummary
        {
            Id = bootstrap.BootstrapCompanyId,
            CompanyCode = bootstrap.BootstrapCompanyCode,
            CompanyName = bootstrap.BootstrapCompanyName,
            BaseCurrencyCode = bootstrap.BootstrapCompanyBaseCurrencyCode,
            MultiCurrencyEnabled = bootstrap.BootstrapCompanyMultiCurrencyEnabled
        };

        AvailableCompanies =
        [
            ActiveCompany
        ];
    }

    public BusinessUserSummary User { get; private set; }

    public BusinessCompanySummary ActiveCompany { get; private set; }

    public IReadOnlyList<BusinessCompanySummary> AvailableCompanies { get; private set; }

    public MaintenanceStateSummary MaintenanceState { get; private set; } = new()
    {
        Enabled = false,
        Message = "Platform runtime is accepting interactive changes."
    };

    public bool IsCompanyReadOnly => ActiveCompany.IsReadOnly;

    public bool AreWritesBlocked => MaintenanceState.Enabled || ActiveCompany.IsReadOnly;

    public string WriteBlockMessage =>
        MaintenanceState.Enabled
            ? MaintenanceState.Message
            : ActiveCompany.IsReadOnly
                ? $"Company {ActiveCompany.CompanyName} is {NormalizeStatus(ActiveCompany.Status)} and currently read-only."
                : "Business writes are available.";

    public IReadOnlyList<NavSection> NavigationSections { get; } =
    [
        new NavSection
        {
            Title = "Core",
            Items =
            [
                new NavMenuItem { Title = "Dashboard", Href = "dashboard", Icon = IconName.LayoutDashboard },
                new NavMenuItem { Title = "Journal Entry", Href = "journal-entry", Icon = IconName.FileText },
                new NavMenuItem { Title = "Invoices", Href = "invoices", Icon = IconName.FileInvoice },
                new NavMenuItem { Title = "Bills", Href = "bills", Icon = IconName.Receipt }
            ]
        },
        new NavSection
        {
            Title = "Sales & Get Paid",
            Items =
            [
                new NavMenuItem { Title = "Customers", Href = "customers", Icon = IconName.Users },
                new NavMenuItem { Title = "Receive Payment", Href = "receive-payment", Icon = IconName.Cash }
            ]
        },
        new NavSection
        {
            Title = "Expense & Bills",
            Items =
            [
                new NavMenuItem { Title = "Vendors", Href = "vendors", Icon = IconName.BuildingStore },
                new NavMenuItem { Title = "Pay Bills", Href = "pay-bills", Icon = IconName.Wallet }
            ]
        },
        new NavSection
        {
            Title = "Accounting",
            Items =
            [
                new NavMenuItem { Title = "Chart of Accounts", Href = "chart-of-accounts", Icon = IconName.BuildingBank },
                new NavMenuItem { Title = "Reconciliation", Href = "reconciliation", Icon = IconName.CircleCheck },
                new NavMenuItem { Title = "Reports", Href = "reports", Icon = IconName.ReportAnalytics }
            ]
        },
        new NavSection
        {
            Title = "Settings",
            Items =
            [
                new NavMenuItem { Title = "Company Settings", Href = "settings", Icon = IconName.Settings },
                new NavMenuItem { Title = "Session", Href = "session", Icon = IconName.User }
            ]
        }
    ];

    public Guid CurrentUserId => User.Id;

    public bool TrySetActiveCompany(Guid companyId)
    {
        var company = AvailableCompanies.FirstOrDefault(candidate => candidate.Id == companyId);
        if (company is null)
        {
            return false;
        }

        ActiveCompany = company;
        return true;
    }

    public void ApplySessionContext(BusinessSessionContextSummary context)
    {
        User = context.User;
        ActiveCompany = context.ActiveCompany;
        AvailableCompanies = context.AvailableCompanies;
        MaintenanceState = context.MaintenanceState;
    }

    private static string NormalizeStatus(string? status) =>
        string.IsNullOrWhiteSpace(status)
            ? "inactive"
            : status.Trim().ToLowerInvariant();
}
