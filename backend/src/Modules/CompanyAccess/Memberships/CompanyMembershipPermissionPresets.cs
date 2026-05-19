namespace Modules.CompanyAccess.Memberships;

/// <summary>
/// Permission presets are not a runtime authorization concept —
/// authorization only ever looks at <c>permissions</c> on a membership.
/// Presets are a SysAdmin-side <i>application tool</i>: one click
/// expands a code into a curated set of fine-grained tokens that
/// gets written verbatim to the target membership's permissions
/// array. The presets do not bind the membership; subsequent
/// per-token adjustments are encouraged.
///
/// Each preset definition also includes <i>every legacy token</i>
/// whose expansion is a subset of the preset, so the membership
/// continues to satisfy legacy <c>Roles.Contains("ar")</c>-style
/// authorization codepaths.
/// </summary>
public static class CompanyMembershipPermissionPresets
{
    public const string Owner = "preset.owner";
    public const string Accountant = "preset.accountant";
    public const string Sales = "preset.sales";
    public const string Bookkeeper = "preset.bookkeeper";
    public const string Viewer = "preset.viewer";
    public const string TaskOnly = "preset.task_only";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Definitions = BuildDefinitions();

    public static IReadOnlyList<CompanyMembershipPermissionPresetOption> Options { get; } =
    [
        new(Owner, "Owner", "Full company access — every fine-grained token plus all legacy tokens."),
        new(Accountant, "Accountant", "Full AR + AP + GL + Reports, plus read access to inventory items and prices."),
        new(Sales, "Sales", "Quote / invoice / customer management plus task creation. Read-only inventory and prices."),
        new(Bookkeeper, "Bookkeeper", "AP bills and payments, vendor master, read-only inventory."),
        new(Viewer, "Viewer", "Read-only across every module."),
        new(TaskOnly, "Task Only", "Just the basic task workflow (view / create / edit / complete)."),
    ];

    public static IReadOnlyList<string> KnownPresets { get; } = Options.Select(static option => option.Code).ToArray();

    /// <summary>
    /// Returns the token set the preset represents (sorted, deduped).
    /// Throws on unknown preset code so callers fail loudly instead of
    /// silently writing an empty permission set.
    /// </summary>
    public static IReadOnlyList<string> Expand(string presetCode)
    {
        if (string.IsNullOrWhiteSpace(presetCode))
        {
            throw new InvalidOperationException("Permission preset code is required.");
        }

        var normalized = presetCode.Trim().ToLowerInvariant();
        if (!Definitions.TryGetValue(normalized, out var tokens))
        {
            throw new InvalidOperationException($"Unknown permission preset '{normalized}'.");
        }

        return tokens;
    }

    public static bool IsKnown(string? presetCode)
    {
        if (string.IsNullOrWhiteSpace(presetCode))
        {
            return false;
        }

        return Definitions.ContainsKey(presetCode.Trim().ToLowerInvariant());
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildDefinitions()
    {
        var owner = CompanyMembershipPermissionCatalog.AllTokens;

        var accountant = Concat(
            // Legacy bridges
            new[]
            {
                CompanyMembershipPermissionCatalog.Ar,
                CompanyMembershipPermissionCatalog.Ap,
                CompanyMembershipPermissionCatalog.Approve,
                CompanyMembershipPermissionCatalog.Reports,
                CompanyMembershipPermissionCatalog.Reconciliation,
            },
            // AR full
            new[]
            {
                CompanyMembershipPermissionCatalog.ArInvoiceView,
                CompanyMembershipPermissionCatalog.ArInvoiceCreate,
                CompanyMembershipPermissionCatalog.ArInvoiceEdit,
                CompanyMembershipPermissionCatalog.ArInvoicePost,
                CompanyMembershipPermissionCatalog.ArInvoiceVoid,
                CompanyMembershipPermissionCatalog.ArInvoiceExport,
                CompanyMembershipPermissionCatalog.ArReceiptView,
                CompanyMembershipPermissionCatalog.ArReceiptCreate,
                CompanyMembershipPermissionCatalog.ArReceiptApply,
                CompanyMembershipPermissionCatalog.ArCreditNoteView,
                CompanyMembershipPermissionCatalog.ArCreditNoteCreate,
                CompanyMembershipPermissionCatalog.ArCustomerView,
                CompanyMembershipPermissionCatalog.ArCustomerCreate,
                CompanyMembershipPermissionCatalog.ArCustomerEdit,
                CompanyMembershipPermissionCatalog.ArCustomerExport,
                CompanyMembershipPermissionCatalog.ArAgingView,
            },
            // AP full
            new[]
            {
                CompanyMembershipPermissionCatalog.ApBillView,
                CompanyMembershipPermissionCatalog.ApBillCreate,
                CompanyMembershipPermissionCatalog.ApBillEdit,
                CompanyMembershipPermissionCatalog.ApBillPost,
                CompanyMembershipPermissionCatalog.ApBillVoid,
                CompanyMembershipPermissionCatalog.ApBillExport,
                CompanyMembershipPermissionCatalog.ApPaymentView,
                CompanyMembershipPermissionCatalog.ApPaymentCreate,
                CompanyMembershipPermissionCatalog.ApPaymentApply,
                CompanyMembershipPermissionCatalog.ApVendorCreditView,
                CompanyMembershipPermissionCatalog.ApVendorCreditCreate,
                CompanyMembershipPermissionCatalog.ApVendorView,
                CompanyMembershipPermissionCatalog.ApVendorCreate,
                CompanyMembershipPermissionCatalog.ApVendorEdit,
                CompanyMembershipPermissionCatalog.ApVendorExport,
                CompanyMembershipPermissionCatalog.ApAgingView,
            },
            // GL full
            new[]
            {
                CompanyMembershipPermissionCatalog.GlJournalView,
                CompanyMembershipPermissionCatalog.GlJournalCreate,
                CompanyMembershipPermissionCatalog.GlJournalPost,
                CompanyMembershipPermissionCatalog.GlAccountView,
                CompanyMembershipPermissionCatalog.GlAccountEdit,
            },
            // Reports
            new[]
            {
                CompanyMembershipPermissionCatalog.ReportsView,
                CompanyMembershipPermissionCatalog.ReportsExport,
                CompanyMembershipPermissionCatalog.ReportsAdvancedView,
            },
            // Inventory read
            new[]
            {
                CompanyMembershipPermissionCatalog.InventoryItemView,
                CompanyMembershipPermissionCatalog.InventoryPriceView,
            },
            // Task margin report (Batch 10): accountants own gross-margin
            // analysis; they need this even when the Task module is
            // otherwise scoped to PMs.
            new[]
            {
                CompanyMembershipPermissionCatalog.TaskReportMargin,
            });

        var sales = Concat(
            new[]
            {
                CompanyMembershipPermissionCatalog.Ar,
            },
            new[]
            {
                CompanyMembershipPermissionCatalog.ArInvoiceView,
                CompanyMembershipPermissionCatalog.ArInvoiceCreate,
                CompanyMembershipPermissionCatalog.ArInvoiceEdit,
                CompanyMembershipPermissionCatalog.ArCreditNoteView,
                CompanyMembershipPermissionCatalog.ArCreditNoteCreate,
                CompanyMembershipPermissionCatalog.ArCustomerView,
                CompanyMembershipPermissionCatalog.ArCustomerCreate,
                CompanyMembershipPermissionCatalog.ArCustomerEdit,
                CompanyMembershipPermissionCatalog.ArAgingView,
            },
            new[]
            {
                CompanyMembershipPermissionCatalog.TaskView,
                CompanyMembershipPermissionCatalog.TaskCreate,
                CompanyMembershipPermissionCatalog.TaskEdit,
                CompanyMembershipPermissionCatalog.TaskComplete,
                CompanyMembershipPermissionCatalog.TaskBill,
                CompanyMembershipPermissionCatalog.TaskReportMargin,
            },
            new[]
            {
                CompanyMembershipPermissionCatalog.InventoryItemView,
                CompanyMembershipPermissionCatalog.InventoryPriceView,
            });

        var bookkeeper = Concat(
            new[]
            {
                CompanyMembershipPermissionCatalog.Ap,
            },
            new[]
            {
                CompanyMembershipPermissionCatalog.ApBillView,
                CompanyMembershipPermissionCatalog.ApBillCreate,
                CompanyMembershipPermissionCatalog.ApBillEdit,
                CompanyMembershipPermissionCatalog.ApBillPost,
                CompanyMembershipPermissionCatalog.ApPaymentView,
                CompanyMembershipPermissionCatalog.ApPaymentCreate,
                CompanyMembershipPermissionCatalog.ApPaymentApply,
                CompanyMembershipPermissionCatalog.ApVendorView,
                CompanyMembershipPermissionCatalog.ApVendorCreate,
                CompanyMembershipPermissionCatalog.ApVendorEdit,
                CompanyMembershipPermissionCatalog.ApAgingView,
            },
            new[]
            {
                CompanyMembershipPermissionCatalog.InventoryItemView,
            });

        // All .view-style tokens — sweep the catalog so future view
        // tokens get picked up automatically without a code update.
        var viewer = CompanyMembershipPermissionCatalog.Options
            .Where(static option =>
                !option.IsLegacy &&
                (option.Token.EndsWith(".view", StringComparison.Ordinal) ||
                 option.Token.EndsWith(".read", StringComparison.Ordinal)))
            .Select(static option => option.Token)
            .ToArray();

        var taskOnly = new[]
        {
            CompanyMembershipPermissionCatalog.TaskView,
            CompanyMembershipPermissionCatalog.TaskCreate,
            CompanyMembershipPermissionCatalog.TaskEdit,
            CompanyMembershipPermissionCatalog.TaskComplete,
        };

        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Owner] = Sorted(owner),
            [Accountant] = Sorted(accountant),
            [Sales] = Sorted(sales),
            [Bookkeeper] = Sorted(bookkeeper),
            [Viewer] = Sorted(viewer),
            [TaskOnly] = Sorted(taskOnly),
        };
    }

    private static IReadOnlyList<string> Concat(params IReadOnlyList<string>[] groups) =>
        groups.SelectMany(static g => g).ToArray();

    private static IReadOnlyList<string> Sorted(IEnumerable<string> tokens) =>
        tokens.Distinct(StringComparer.Ordinal)
              .OrderBy(static t => t, StringComparer.Ordinal)
              .ToArray();
}
