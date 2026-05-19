namespace Modules.CompanyAccess.Memberships;

/// <summary>
/// Single source of truth for valid company-membership permission tokens.
///
/// The catalog has two generations of tokens living side-by-side:
///
/// <list type="bullet">
///   <item><b>Legacy (8 tokens)</b> — the original coarse permission
///     model (<c>ar</c>, <c>ap</c>, <c>approve</c>, ...). Existing
///     workflows still read these; they remain in the catalog and in
///     every membership row so authorization decisions written
///     against them continue to work.</item>
///   <item><b>Fine-grained (Batch 3)</b> — module.resource.action
///     tokens (<c>ar.invoice.create</c>, <c>task.view</c>, ...) that
///     drive the new <c>[HasPermission]</c> decorator and the
///     <see cref="CompanyMembershipPermissionPresets"/> infrastructure.
///     New code should target these; legacy tokens will retire in a
///     later batch once all consumers have migrated.</item>
/// </list>
///
/// <see cref="CompanyMembershipPermissionLegacyExpansion"/> defines
/// the mapping the one-time migration uses to add the fine-grained
/// equivalents to every membership that holds a legacy token, so the
/// two generations stay coherent without manual intervention.
/// </summary>
public static class CompanyMembershipPermissionCatalog
{
    // ─── Legacy coarse tokens (still valid; mark `IsLegacy=true`) ──
    public const string Ar = "ar";
    public const string Ap = "ap";
    public const string Approve = "approve";
    public const string Reports = "reports";
    public const string SettingsAccess = "settings_access";
    public const string CompanyAccountingSettings = "company_accounting_settings";
    public const string CompanyBookGovernance = "company_book_governance";
    public const string Reconciliation = "reconciliation";

    // ─── AR fine-grained ───────────────────────────────────────────
    public const string ArInvoiceView = "ar.invoice.view";
    public const string ArInvoiceCreate = "ar.invoice.create";
    public const string ArInvoiceEdit = "ar.invoice.edit";
    public const string ArInvoicePost = "ar.invoice.post";
    public const string ArInvoiceVoid = "ar.invoice.void";
    public const string ArInvoiceExport = "ar.invoice.export";
    public const string ArReceiptView = "ar.receipt.view";
    public const string ArReceiptCreate = "ar.receipt.create";
    public const string ArReceiptApply = "ar.receipt.apply";
    public const string ArCreditNoteView = "ar.creditnote.view";
    public const string ArCreditNoteCreate = "ar.creditnote.create";
    public const string ArCustomerView = "ar.customer.view";
    public const string ArCustomerCreate = "ar.customer.create";
    public const string ArCustomerEdit = "ar.customer.edit";
    public const string ArCustomerExport = "ar.customer.export";
    public const string ArAgingView = "ar.aging.view";

    // ─── AP fine-grained ───────────────────────────────────────────
    public const string ApBillView = "ap.bill.view";
    public const string ApBillCreate = "ap.bill.create";
    public const string ApBillEdit = "ap.bill.edit";
    public const string ApBillPost = "ap.bill.post";
    public const string ApBillVoid = "ap.bill.void";
    public const string ApBillExport = "ap.bill.export";
    public const string ApPaymentView = "ap.payment.view";
    public const string ApPaymentCreate = "ap.payment.create";
    public const string ApPaymentApply = "ap.payment.apply";
    public const string ApVendorCreditView = "ap.vendorcredit.view";
    public const string ApVendorCreditCreate = "ap.vendorcredit.create";
    public const string ApVendorView = "ap.vendor.view";
    public const string ApVendorCreate = "ap.vendor.create";
    public const string ApVendorEdit = "ap.vendor.edit";
    public const string ApVendorExport = "ap.vendor.export";
    public const string ApAgingView = "ap.aging.view";

    // ─── Inventory / Products & Services ───────────────────────────
    public const string InventoryItemView = "inventory.item.view";
    public const string InventoryItemCreate = "inventory.item.create";
    public const string InventoryItemEdit = "inventory.item.edit";
    public const string InventoryItemExport = "inventory.item.export";
    public const string InventoryPriceView = "inventory.price.view";
    public const string InventoryPriceEdit = "inventory.price.edit";
    public const string InventoryWarehouseView = "inventory.warehouse.view";
    public const string InventoryWarehouseEdit = "inventory.warehouse.edit";
    public const string InventoryStockView = "inventory.stock.view";
    public const string InventoryStockAdjust = "inventory.stock.adjust";

    // ─── GL ────────────────────────────────────────────────────────
    public const string GlJournalView = "gl.journal.view";
    public const string GlJournalCreate = "gl.journal.create";
    public const string GlJournalPost = "gl.journal.post";
    public const string GlAccountView = "gl.account.view";
    public const string GlAccountEdit = "gl.account.edit";
    public const string GlPeriodClose = "gl.period.close";

    // ─── Reports ───────────────────────────────────────────────────
    public const string ReportsView = "reports.view";
    public const string ReportsExport = "reports.export";
    public const string ReportsAdvancedView = "reports.advanced.view";

    // ─── Task (Batch 5 will consume) ───────────────────────────────
    public const string TaskView = "task.view";
    public const string TaskViewAll = "task.view.all";
    public const string TaskCreate = "task.create";
    public const string TaskEdit = "task.edit";
    public const string TaskComplete = "task.complete";
    public const string TaskCancel = "task.cancel";
    public const string TaskBill = "task.bill";
    public const string TaskExport = "task.export";
    public const string TaskArchiveRead = "task.archive.read";
    public const string TaskReportMargin = "task.report.margin";

    // ─── Settings (meta-permissions) ───────────────────────────────
    public const string SettingsCompanyView = "settings.company.view";
    public const string SettingsCompanyEdit = "settings.company.edit";
    public const string SettingsPermissionsView = "settings.permissions.view";
    public const string SettingsPermissionsAssign = "settings.permissions.assign";
    public const string SettingsModulesToggle = "settings.modules.toggle";
    public const string SettingsNumberingEdit = "settings.numbering.edit";
    public const string SettingsFxEdit = "settings.fx.edit";
    public const string SettingsTaxEdit = "settings.tax.edit";

    public static IReadOnlyList<CompanyMembershipPermissionOption> Options { get; } =
    [
        // Legacy ─ kept for backward-compatible authorization codepaths.
        new(Ar, "AR", "Legacy coarse: AR module access.", IsGovernancePermission: false, IsLegacy: true),
        new(Ap, "AP", "Legacy coarse: AP module access.", IsGovernancePermission: false, IsLegacy: true),
        new(Approve, "Approve", "Legacy coarse: posting / approval rights.", IsGovernancePermission: false, IsLegacy: true),
        new(Reports, "Reports", "Legacy coarse: report surfaces.", IsGovernancePermission: false, IsLegacy: true),
        new(SettingsAccess, "Settings Access", "Legacy coarse: company settings entry.", IsGovernancePermission: false, IsLegacy: true),
        new(CompanyAccountingSettings, "Company Accounting Settings", "Legacy governance: numbering / tax / FX edit.", IsGovernancePermission: true, IsLegacy: true),
        new(CompanyBookGovernance, "Book Governance", "Legacy governance: permissions, modules, period close.", IsGovernancePermission: true, IsLegacy: true),
        new(Reconciliation, "Reconciliation", "Legacy coarse: reconciliation workflows.", IsGovernancePermission: false, IsLegacy: true),

        // AR
        new(ArInvoiceView, "AR · Invoice · View", "View AR invoices.", false),
        new(ArInvoiceCreate, "AR · Invoice · Create", "Create AR invoices.", false),
        new(ArInvoiceEdit, "AR · Invoice · Edit", "Edit AR invoices in draft.", false),
        new(ArInvoicePost, "AR · Invoice · Post", "Post AR invoices to the ledger.", false),
        new(ArInvoiceVoid, "AR · Invoice · Void", "Void posted AR invoices.", false),
        new(ArInvoiceExport, "AR · Invoice · Export", "Export AR invoice lists.", false),
        new(ArReceiptView, "AR · Receipt · View", "View customer receipts.", false),
        new(ArReceiptCreate, "AR · Receipt · Create", "Create customer receipts.", false),
        new(ArReceiptApply, "AR · Receipt · Apply", "Apply receipts to invoices.", false),
        new(ArCreditNoteView, "AR · Credit Note · View", "View AR credit notes.", false),
        new(ArCreditNoteCreate, "AR · Credit Note · Create", "Create AR credit notes.", false),
        new(ArCustomerView, "AR · Customer · View", "View customer master.", false),
        new(ArCustomerCreate, "AR · Customer · Create", "Create customers.", false),
        new(ArCustomerEdit, "AR · Customer · Edit", "Edit customers.", false),
        new(ArCustomerExport, "AR · Customer · Export", "Export customer lists.", false),
        new(ArAgingView, "AR · Aging · View", "View AR aging reports.", false),

        // AP
        new(ApBillView, "AP · Bill · View", "View AP bills.", false),
        new(ApBillCreate, "AP · Bill · Create", "Create AP bills.", false),
        new(ApBillEdit, "AP · Bill · Edit", "Edit AP bills in draft.", false),
        new(ApBillPost, "AP · Bill · Post", "Post AP bills to the ledger.", false),
        new(ApBillVoid, "AP · Bill · Void", "Void posted AP bills.", false),
        new(ApBillExport, "AP · Bill · Export", "Export AP bill lists.", false),
        new(ApPaymentView, "AP · Payment · View", "View vendor payments.", false),
        new(ApPaymentCreate, "AP · Payment · Create", "Create vendor payments.", false),
        new(ApPaymentApply, "AP · Payment · Apply", "Apply payments to bills.", false),
        new(ApVendorCreditView, "AP · Vendor Credit · View", "View vendor credits.", false),
        new(ApVendorCreditCreate, "AP · Vendor Credit · Create", "Create vendor credits.", false),
        new(ApVendorView, "AP · Vendor · View", "View vendor master.", false),
        new(ApVendorCreate, "AP · Vendor · Create", "Create vendors.", false),
        new(ApVendorEdit, "AP · Vendor · Edit", "Edit vendors.", false),
        new(ApVendorExport, "AP · Vendor · Export", "Export vendor lists.", false),
        new(ApAgingView, "AP · Aging · View", "View AP aging reports.", false),

        // Inventory
        new(InventoryItemView, "Inventory · Item · View", "View product / service master.", false),
        new(InventoryItemCreate, "Inventory · Item · Create", "Create products / services.", false),
        new(InventoryItemEdit, "Inventory · Item · Edit", "Edit products / services.", false),
        new(InventoryItemExport, "Inventory · Item · Export", "Export product / service lists.", false),
        new(InventoryPriceView, "Inventory · Price · View", "View item prices.", false),
        new(InventoryPriceEdit, "Inventory · Price · Edit", "Edit item prices.", false),
        new(InventoryWarehouseView, "Inventory · Warehouse · View", "View warehouses.", false),
        new(InventoryWarehouseEdit, "Inventory · Warehouse · Edit", "Edit warehouses.", false),
        new(InventoryStockView, "Inventory · Stock · View", "View on-hand stock.", false),
        new(InventoryStockAdjust, "Inventory · Stock · Adjust", "Adjust stock quantities.", false),

        // GL
        new(GlJournalView, "GL · Journal · View", "View journal entries.", false),
        new(GlJournalCreate, "GL · Journal · Create", "Create manual journal entries.", false),
        new(GlJournalPost, "GL · Journal · Post", "Post journal entries.", false),
        new(GlAccountView, "GL · Account · View", "View chart of accounts.", false),
        new(GlAccountEdit, "GL · Account · Edit", "Edit chart of accounts.", false),
        new(GlPeriodClose, "GL · Period · Close", "Close accounting periods.", IsGovernancePermission: true),

        // Reports
        new(ReportsView, "Reports · View", "View standard reports.", false),
        new(ReportsExport, "Reports · Export", "Export reports.", false),
        new(ReportsAdvancedView, "Reports · Advanced · View", "View advanced / sensitive reports.", false),

        // Task (Batch 5)
        new(TaskView, "Task · View", "View tasks (own assignments).", false),
        new(TaskViewAll, "Task · View All", "View every task in the company.", false),
        new(TaskCreate, "Task · Create", "Create tasks.", false),
        new(TaskEdit, "Task · Edit", "Edit tasks in open state.", false),
        new(TaskComplete, "Task · Complete", "Mark tasks as completed.", false),
        new(TaskCancel, "Task · Cancel", "Cancel tasks.", false),
        new(TaskBill, "Task · Bill", "Push completed tasks into AR invoice flow.", false),
        new(TaskExport, "Task · Export", "Export task lists.", false),
        new(TaskArchiveRead, "Task · Archive · Read", "Read archived task data after module is disabled.", IsGovernancePermission: true),
        new(TaskReportMargin, "Task · Report · Margin", "View operational and billed gross-margin reports.", false),

        // Settings
        new(SettingsCompanyView, "Settings · Company · View", "View company settings.", false),
        new(SettingsCompanyEdit, "Settings · Company · Edit", "Edit company settings.", IsGovernancePermission: true),
        new(SettingsPermissionsView, "Settings · Permissions · View", "View permissions assignments.", IsGovernancePermission: true),
        new(SettingsPermissionsAssign, "Settings · Permissions · Assign", "Grant / revoke permissions on memberships.", IsGovernancePermission: true),
        new(SettingsModulesToggle, "Settings · Modules · Toggle", "Enable / disable optional modules.", IsGovernancePermission: true),
        new(SettingsNumberingEdit, "Settings · Numbering · Edit", "Edit document numbering policy.", IsGovernancePermission: true),
        new(SettingsFxEdit, "Settings · FX · Edit", "Edit FX policy.", IsGovernancePermission: true),
        new(SettingsTaxEdit, "Settings · Tax · Edit", "Edit tax codes.", IsGovernancePermission: true),
    ];

    public static IReadOnlyList<string> AllTokens { get; } =
        Options.Select(static option => option.Token).ToArray();

    public static IReadOnlyList<string> FineGrainedTokens { get; } =
        Options.Where(static option => !option.IsLegacy).Select(static option => option.Token).ToArray();

    public static IReadOnlyList<string> LegacyTokens { get; } =
        Options.Where(static option => option.IsLegacy).Select(static option => option.Token).ToArray();

    public static IReadOnlyList<string> NormalizeTokens(IEnumerable<string> tokens)
    {
        var allowed = Options
            .Select(static option => option.Token)
            .ToHashSet(StringComparer.Ordinal);

        var normalized = tokens
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .Select(static token => token.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static token => token, StringComparer.Ordinal)
            .ToArray();

        var unknown = normalized.FirstOrDefault(token => !allowed.Contains(token));
        if (unknown is not null)
        {
            throw new InvalidOperationException($"Unknown company membership permission token '{unknown}'.");
        }

        return normalized;
    }
}
