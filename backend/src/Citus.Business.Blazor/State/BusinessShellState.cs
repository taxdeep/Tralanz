using Citus.Business.Blazor.Configuration;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Navigation;
using Citus.Ui.Shared.Shell;
using Microsoft.Extensions.Options;
using MudBlazor;

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

    public IReadOnlyList<NavSection> NavigationSections { get; } =
    [
        new NavSection
        {
            Title = "Core",
            Items =
            [
                new NavMenuItem { Title = "Dashboard", Href = "dashboard", Icon = Icons.Material.Filled.Dashboard },
                new NavMenuItem { Title = "Journal Entry", Href = "journal-entry", Icon = Icons.Material.Filled.LibraryBooks },
                new NavMenuItem { Title = "Invoices", Href = "invoices", Icon = Icons.Material.Filled.ReceiptLong },
                new NavMenuItem { Title = "Bills", Href = "bills", Icon = Icons.Material.Filled.RequestQuote }
            ]
        },
        new NavSection
        {
            Title = "Sales & Get Paid",
            Items =
            [
                new NavMenuItem { Title = "Customers", Href = "customers", Icon = Icons.Material.Filled.Groups },
                new NavMenuItem { Title = "Receive Payment", Href = "receive-payment", Icon = Icons.Material.Filled.Payments }
            ]
        },
        new NavSection
        {
            Title = "Expense & Bills",
            Items =
            [
                new NavMenuItem { Title = "Vendors", Href = "vendors", Icon = Icons.Material.Filled.Storefront },
                new NavMenuItem { Title = "Pay Bills", Href = "pay-bills", Icon = Icons.Material.Filled.AccountBalanceWallet }
            ]
        },
        new NavSection
        {
            Title = "Accounting",
            Items =
            [
                new NavMenuItem { Title = "Chart of Accounts", Href = "chart-of-accounts", Icon = Icons.Material.Filled.AccountTree },
                new NavMenuItem { Title = "Reconciliation", Href = "reconciliation", Icon = Icons.Material.Filled.RuleFolder },
                new NavMenuItem { Title = "Reports", Href = "reports", Icon = Icons.Material.Filled.Assessment }
            ]
        },
        new NavSection
        {
            Title = "Settings",
            Items =
            [
                new NavMenuItem { Title = "Company Settings", Href = "settings", Icon = Icons.Material.Filled.Settings },
                new NavMenuItem { Title = "Session", Href = "session", Icon = Icons.Material.Filled.Badge }
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
}
