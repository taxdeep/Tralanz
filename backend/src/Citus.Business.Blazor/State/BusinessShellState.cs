using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Icons;
using Citus.Ui.Shared.Navigation;
using Citus.Ui.Shared.Shell;

namespace Citus.Business.Blazor.State;

public sealed class BusinessShellState
{
    public BusinessShellState()
    {
        User = BuildSignedOutUser();
        ActiveCompany = BuildSignedOutCompany();
        AvailableCompanies = Array.Empty<BusinessCompanySummary>();
    }

    public BusinessUserSummary User { get; private set; }

    public BusinessCompanySummary ActiveCompany { get; private set; }

    public IReadOnlyList<BusinessCompanySummary> AvailableCompanies { get; private set; }

    public string SessionToken { get; private set; } = string.Empty;

    public bool IsBootstrapSession { get; private set; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(SessionToken);

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
                new NavMenuItem { Title = "Quotes", Href = "quotes", Icon = IconName.FileText },
                new NavMenuItem { Title = "Sales Orders", Href = "sales-orders", Icon = IconName.Receipt },
                new NavMenuItem { Title = "Receive Payment", Href = "receive-payment", Icon = IconName.Cash }
            ]
        },
        new NavSection
        {
            Title = "Expense & Bills",
            Items =
            [
                new NavMenuItem { Title = "Vendors", Href = "vendors", Icon = IconName.BuildingStore },
                new NavMenuItem { Title = "Purchase Orders", Href = "purchase-orders", Icon = IconName.FileText },
                new NavMenuItem { Title = "Bills", Href = "bills", Icon = IconName.Receipt },
                new NavMenuItem { Title = "Expenses", Href = "expenses", Icon = IconName.Wallet },
                new NavMenuItem { Title = "Pay Bills", Href = "pay-bills", Icon = IconName.Wallet }
            ]
        },
        new NavSection
        {
            Title = "Catalog",
            Items =
            [
                new NavMenuItem { Title = "Products & Services", Href = "items", Icon = IconName.Puzzle }
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
                new NavMenuItem { Title = "Profile", Href = "settings/profile", Icon = IconName.BuildingSkyscraper },
                new NavMenuItem { Title = "Currencies", Href = "settings/currencies", Icon = IconName.Coin },
                new NavMenuItem { Title = "Fiscal Year", Href = "settings/fiscal-year", Icon = IconName.Calendar },
                new NavMenuItem { Title = "Tax Rates", Href = "settings/tax-rates", Icon = IconName.Receipt },
                new NavMenuItem { Title = "Payment Terms", Href = "settings/payment-terms", Icon = IconName.Calendar },
                new NavMenuItem { Title = "Numbering", Href = "settings/numbering", Icon = IconName.FileInvoice },
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

    public void ApplyAuthenticatedSession(
        string sessionToken,
        BusinessAuthSessionSummary session,
        bool isBootstrap)
    {
        SessionToken = sessionToken.Trim();
        IsBootstrapSession = isBootstrap;
        User = session.User;
        ActiveCompany = session.ActiveCompany;
        AvailableCompanies = session.AvailableCompanies.Count > 0
            ? session.AvailableCompanies
            : new[] { session.ActiveCompany };
    }

    /// <summary>
    /// Replaces the signed-in user's display name without touching any
    /// other identity fields. Used after a successful profile save so the
    /// topbar / dropdown reflect the new name immediately, without a
    /// full session round-trip.
    /// </summary>
    public void UpdateUserDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return;
        }

        User = User with { DisplayName = displayName.Trim() };
    }

    public void ClearAuthenticatedSession()
    {
        SessionToken = string.Empty;
        IsBootstrapSession = false;
        User = BuildSignedOutUser();
        ActiveCompany = BuildSignedOutCompany();
        AvailableCompanies = Array.Empty<BusinessCompanySummary>();
        MaintenanceState = new MaintenanceStateSummary
        {
            Enabled = false,
            Message = "Platform runtime is accepting interactive changes."
        };
    }

    private BusinessUserSummary BuildSignedOutUser() => new()
    {
        Id = Guid.Empty,
        DisplayName = "Guest",
        Email = string.Empty,
        Username = string.Empty,
        Roles = Array.Empty<string>()
    };

    private BusinessCompanySummary BuildSignedOutCompany() => new()
    {
        Id = Guid.Empty,
        CompanyCode = string.Empty,
        CompanyName = string.Empty,
        BaseCurrencyCode = string.Empty,
        MultiCurrencyEnabled = false
    };

    private static string NormalizeStatus(string? status) =>
        string.IsNullOrWhiteSpace(status)
            ? "inactive"
            : status.Trim().ToLowerInvariant();
}
