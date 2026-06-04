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

    public IReadOnlyDictionary<string, bool> ModuleFlags { get; private set; } =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>
    /// Raised after any state mutation that affects derived UI surfaces
    /// (BusinessNavMenu, CreateNewButton, etc.). Components that render
    /// from ShellState should subscribe in OnInitialized and call
    /// StateHasChanged in the handler so toggling a module flag, switching
    /// companies, or applying a new session updates the sidebar without
    /// a page refresh.
    /// </summary>
    public event Action? OnChanged;

    /// <summary>
    /// True when the named per-company module flag is on. Drives the
    /// nav menu and any in-page "Send to {module}" affordances.
    /// Defaults to false (fail-closed) for unknown keys so a missed
    /// module-flag fetch never silently exposes a hidden module.
    /// </summary>
    public bool IsModuleEnabled(string moduleKey)
    {
        if (string.IsNullOrWhiteSpace(moduleKey)) return true;
        return ModuleFlags.TryGetValue(moduleKey.Trim().ToLowerInvariant(), out var enabled) && enabled;
    }

    public void ApplyModuleFlags(IReadOnlyDictionary<string, bool> flags)
    {
        ModuleFlags = flags;
        OnChanged?.Invoke();
    }

    public IReadOnlyList<NavSection> NavigationSections { get; } =
    [
        new NavSection
        {
            Title = "Core",
            Items =
            [
                new NavMenuItem { Title = "Dashboard", Href = "dashboard", Icon = IconName.LayoutDashboard },
                new NavMenuItem { Title = "Invoices", Href = "invoices", Icon = IconName.FileInvoice },
                new NavMenuItem { Title = "Bills", Href = "bills", Icon = IconName.Receipt }
            ]
        },
        new NavSection
        {
            // Per-company opt-in module. Every item below carries
            // ModuleKey="task"; BusinessNavMenu hides items whose
            // ModuleKey is set but not enabled. The whole section
            // disappears when none of its items survive the filter.
            Title = "Tasks",
            Items =
            [
                new NavMenuItem { Title = "Tasks", Href = "tasks", Icon = IconName.Activity, ModuleKey = "task" },
                new NavMenuItem { Title = "Margin Report", Href = "tasks/reports/margin", Icon = IconName.ReportAnalytics, ModuleKey = "task" }
            ]
        },
        new NavSection
        {
            Title = "Sales & Get Paid",
            Items =
            [
                new NavMenuItem { Title = "Overview", Href = "sales-overview", Icon = IconName.LayoutDashboard },
                new NavMenuItem { Title = "Customers", Href = "customers", Icon = IconName.Users },
                new NavMenuItem { Title = "Quotes", Href = "quotes", Icon = IconName.FileText },
                new NavMenuItem { Title = "Sales Orders", Href = "sales-orders", Icon = IconName.Receipt },
                new NavMenuItem { Title = "Sales Receipts", Href = "sales-receipts", Icon = IconName.Cash },
                new NavMenuItem { Title = "Receive Payment", Href = "receive-payment", Icon = IconName.Cash },
                new NavMenuItem { Title = "Credit Memos", Href = "credit-memos", Icon = IconName.FileText },
                new NavMenuItem { Title = "Refund Receipts", Href = "refund-receipts", Icon = IconName.ArrowLeft }
            ]
        },
        new NavSection
        {
            Title = "Expense & Bills",
            Items =
            [
                new NavMenuItem { Title = "Overview", Href = "expense-overview", Icon = IconName.LayoutDashboard },
                new NavMenuItem { Title = "Vendors", Href = "vendors", Icon = IconName.BuildingStore },
                new NavMenuItem { Title = "Purchase Orders", Href = "purchase-orders", Icon = IconName.FileText },
                new NavMenuItem { Title = "Bills", Href = "bills", Icon = IconName.Receipt },
                new NavMenuItem { Title = "Expenses", Href = "expenses", Icon = IconName.Wallet },
                new NavMenuItem { Title = "Pay Bills", Href = "pay-bills", Icon = IconName.Wallet },
                new NavMenuItem { Title = "Vendor Credits", Href = "vendor-credits", Icon = IconName.FileText }
            ]
        },
        new NavSection
        {
            Title = "Warehouse",
            Items =
            [
                new NavMenuItem { Title = "Products & Services", Href = "items", Icon = IconName.Puzzle },
                new NavMenuItem { Title = "Warehouses", Href = "company/warehouses", Icon = IconName.BuildingStore },
                new NavMenuItem { Title = "Sales Issue COGS", Href = "company/inventory/cogs-postings", Icon = IconName.Receipt },
                new NavMenuItem { Title = "Drop-ship Clearing", Href = "company/inventory/drop-ship-clearing", Icon = IconName.Truck }
                // "Inventory Setup" intentionally not surfaced in the
                // sidebar — the /company/warehouses page exposes a
                // Setup button to operators whose company has already
                // activated the module, and the activation wizard for
                // a fresh company is gated by
                // FeatureFlags.InventoryActivationEntryEnabled. See
                // commit b827c5d (Stage 1.1).
            ]
        },
        new NavSection
        {
            Title = "Banking",
            Items =
            [
                new NavMenuItem { Title = "Bank Register", Href = "banking/register", Icon = IconName.BuildingBank },
                new NavMenuItem { Title = "Reconciliation", Href = "reconciliation", Icon = IconName.CircleCheck },
                new NavMenuItem { Title = "Account Transfers", Href = "bank-transfers", Icon = IconName.ArrowLeft },
                new NavMenuItem { Title = "Bank Deposits", Href = "bank-deposits", Icon = IconName.Cash }
            ]
        },
        new NavSection
        {
            Title = "Accounting",
            Items =
            [
                new NavMenuItem { Title = "Chart of Accounts", Href = "chart-of-accounts", Icon = IconName.BuildingBank },
                new NavMenuItem { Title = "Journal Entry", Href = "journal-entry", Icon = IconName.FileText },
                new NavMenuItem { Title = "Reports", Href = "reports", Icon = IconName.ReportAnalytics }
            ]
        },
        new NavSection
        {
            // Items already surfaced as Live "domain cards" on
            // /settings (CompanySettingsPage) are intentionally NOT
            // duplicated here — Profile / Currencies / Fiscal Year /
            // Tax Rates / Payment Terms / Numbering / Invoice
            // Templates all live behind the Company Settings entry.
            // The nav keeps only items that don't have a card OR
            // (Tax Returns) live outside /settings entirely.
            Title = "Settings",
            Items =
            [
                new NavMenuItem { Title = "Company Settings", Href = "settings", Icon = IconName.Settings },
                new NavMenuItem { Title = "Modules", Href = "settings/modules", Icon = IconName.Puzzle },
                new NavMenuItem { Title = "Accounting Periods", Href = "settings/accounting-periods", Icon = IconName.Clock },
                new NavMenuItem { Title = "Year-end Pre-close", Href = "settings/year-end-pre-close", Icon = IconName.AlertCircle },
                new NavMenuItem { Title = "Member Permissions", Href = "settings/permissions", Icon = IconName.ShieldLock },
                new NavMenuItem { Title = "Audit Logs", Href = "settings/audit-logs", Icon = IconName.Eye },
                new NavMenuItem { Title = "Tax Returns", Href = "tax-returns", Icon = IconName.ReportAnalytics }
                // /session diagnostics page stays reachable by direct
                // URL but is no longer surfaced in the sidebar — it's a
                // debug-style identity dump, not a routine action.
            ]
        }
    ];

    public UserId CurrentUserId => User.Id;

    public bool TrySetActiveCompany(CompanyId companyId)
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
        OnChanged?.Invoke();
    }

    public void ApplyAuthenticatedSession(
        string sessionToken,
        BusinessAuthSessionSummary session)
    {
        SessionToken = sessionToken.Trim();
        User = session.User;
        ActiveCompany = session.ActiveCompany;
        AvailableCompanies = session.AvailableCompanies.Count > 0
            ? session.AvailableCompanies
            : new[] { session.ActiveCompany };
        OnChanged?.Invoke();
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
        OnChanged?.Invoke();
    }

    public void ClearAuthenticatedSession()
    {
        SessionToken = string.Empty;
        User = BuildSignedOutUser();
        ActiveCompany = BuildSignedOutCompany();
        AvailableCompanies = Array.Empty<BusinessCompanySummary>();
        MaintenanceState = new MaintenanceStateSummary
        {
            Enabled = false,
            Message = "Platform runtime is accepting interactive changes."
        };
        ModuleFlags = new Dictionary<string, bool>(StringComparer.Ordinal);
        OnChanged?.Invoke();
    }

    private BusinessUserSummary BuildSignedOutUser() => new()
    {
        Id = default,
        DisplayName = "Guest",
        Email = string.Empty,
        Username = string.Empty,
        Roles = Array.Empty<string>()
    };

    private BusinessCompanySummary BuildSignedOutCompany() => new()
    {
        Id = default,
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
