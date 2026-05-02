using Citus.Accounting.Application.Abstractions;

namespace Citus.Accounting.Application.CoaTemplates;

/// <summary>
/// In-process registry of starter CoA templates surfaced by the
/// <c>/accounts/templates</c> + <c>/accounts/templates/{key}/apply</c>
/// endpoints (the post-wizard "Seed from template" affordance on the
/// Chart of Accounts page). The First-Company Wizard runs its own
/// embedded canonical chart inside
/// <c>PostgresPlatformFirstCompanyProvisioningRepository</c> — the
/// content here mirrors that repository's <c>BuildCanonicalChart()</c>
/// so a company that didn't seed at provisioning time, then later
/// chooses to seed manually, gets the same accounts.
/// </summary>
public sealed class StaticCoaTemplateRegistry : ICoaTemplateRegistry
{
    private readonly IReadOnlyDictionary<string, CoaTemplate> _byKey;
    private readonly IReadOnlyList<CoaTemplate> _ordered;

    public StaticCoaTemplateRegistry()
    {
        _ordered = BuildTemplates();
        _byKey = _ordered.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CoaTemplate> List() => _ordered;

    public CoaTemplate? Get(string templateKey) =>
        !string.IsNullOrWhiteSpace(templateKey) && _byKey.TryGetValue(templateKey.Trim(), out var template)
            ? template
            : null;

    private static IReadOnlyList<CoaTemplate> BuildTemplates() =>
        new[]
        {
            BuildCanonical5Digit(),
        };

    /// <summary>
    /// Generic small-business chart at 5-digit canonical length. Cleaned up
    /// 2026-04-27: prior 64-account variant included company-specific bank
    /// names, region-specific tax payables, and industry-specific fixed
    /// assets (logistics tractors / warehouse equipment). This version
    /// targets ~46 user-visible universal accounts plus 5 hidden FX system
    /// rows, with payroll / bad-debt / long-term-debt / income-tax coverage
    /// that the previous chart missed.
    ///
    /// Note (2026-04-27): the prior 11001 / 20001 sub-currency reserve rows
    /// were removed. AR/AP foreign-currency control accounts are now created
    /// on demand by the multi-currency seeder when a company enables an
    /// additional currency (so a USD-enabled company gets "Accounts
    /// Receivable - USD" / "Accounts Payable - USD" allocated into the
    /// 11xxx / 20xxx reserve families).
    ///
    /// All canonical codes are exactly 5 digits. The Wizard's account
    /// code length picker (4–10) reformats them on apply: trailing zeros
    /// are dropped for shorter lengths and appended for longer ones, so
    /// 10000 Cash on Hand becomes 1000 at length 4, 100000 at length 6,
    /// 1000000 at length 7, etc.
    /// </summary>
    private static CoaTemplate BuildCanonical5Digit() => new(
        Key: "ca_general_small_business",
        Version: "5",
        Name: "General business (5-digit canonical)",
        Description: "Generic universal chart of accounts for SMBs. Covers cash (incl. Undeposited Funds for Bank Deposit clearing), AR/AP control (base currency only — per-currency AR/AP rows are added when multi-currency is enabled), current/fixed assets, current and long-term liabilities (incl. credit card, payroll, sales-tax payable + receivable + filing-side rows for TaxReturn period close, income tax), equity, income, COGS, operating expense (incl. payroll), FX, asset disposal, and income tax expense, plus engine-required hidden FX system rows. Codes are stored at 5 digits and shifted to the company's chosen length (4–10).",
        Country: "Generic",
        AccountCodeLength: 5,
        Accounts: new CoaTemplateAccount[]
        {
            // ---- Bank / Cash (10000-10999) ----
            new("10000", "Cash on Hand", "asset", DetailType: "cash"),
            new("10100", "Bank Operating Account", "asset", DetailType: "bank"),

            // ---- Accounts Receivable (11000 base; 11001-11099 reserved for per-currency rows) ----
            new("11000", "Accounts Receivable", "asset",
                DetailType: "accounts_receivable",
                AllowManualPosting: false,
                SystemKey: "control_account:accounts_receivable:base",
                SystemRole: "accounts_receivable"),

            // ---- Other Current Asset (12000-13999) ----
            new("12000", "Undeposited Funds", "asset",
                DetailType: "undeposited_funds",
                SystemKey: "cash:undeposited_funds",
                SystemRole: "undeposited_funds"),
            new("12500", "Short-Term Investments", "asset", DetailType: "short_term_investments"),
            new("12800", "Employee Advances", "asset", DetailType: "employee_advances"),
            new("13100", "Prepaid Expenses", "asset", DetailType: "prepaids"),

            // ---- Sales-tax receivable family (13700-13701) ----
            // Used by the TaxReturn posting fragment to clear ITC
            // accruals (13700) and land net refunds (13701) at
            // period-close time. Operator never touches them
            // directly; AllowManualPosting stays true so adjustment
            // journals can still hit them when needed.
            new("13700", "Sales Tax Receivable", "asset",
                DetailType: "tax",
                SystemKey: "tax:receivable",
                SystemRole: "tax_receivable"),
            new("13701", "Sales Tax Filing Receivable", "asset",
                DetailType: "tax",
                SystemKey: "tax:filing_receivable",
                SystemRole: "tax_filing_receivable"),

            // ---- Inventory (14000) ----
            new("14000", "Inventory", "asset", DetailType: "inventory"),

            // ---- Fixed Asset (15000-17000) ----
            new("15000", "Furniture and Equipment", "asset", DetailType: "fixed_asset"),
            new("15200", "Buildings and Improvements", "asset", DetailType: "fixed_asset"),
            new("15400", "Computer Equipment", "asset", DetailType: "fixed_asset"),
            new("15600", "Land", "asset", DetailType: "fixed_asset"),
            new("15900", "Leasehold Improvements", "asset", DetailType: "fixed_asset"),
            new("16400", "Vehicles", "asset", DetailType: "fixed_asset"),
            new("17000", "Accumulated Depreciation", "asset", DetailType: "contra_asset"),

            // ---- Other Asset (18700) ----
            new("18700", "Security Deposits Asset", "asset", DetailType: "other_asset"),

            // ---- Accounts Payable (20000 base; 20001-20099 reserved for per-currency rows) ----
            new("20000", "Accounts Payable", "liability",
                DetailType: "accounts_payable",
                AllowManualPosting: false,
                SystemKey: "control_account:accounts_payable:base",
                SystemRole: "accounts_payable"),

            // ---- Credit Card Payable (21000) ----
            new("21000", "Credit Card Payable", "liability", DetailType: "credit_card"),

            // ---- Other Current Liability (24000-26999) ----
            new("24000", "Shareholder Loan", "liability", DetailType: "shareholder_loan"),
            new("24700", "Customer Deposits", "liability", DetailType: "customer_deposits"),
            // 25000 holds output-tax accruals — every invoice / sales
            // receipt with a tax code credits it; TaxReturn clears it
            // each period. SystemRole pins the canonical resolution.
            new("25000", "Sales Tax Payable", "liability",
                DetailType: "tax",
                SystemKey: "tax:payable",
                SystemRole: "tax_payable"),
            // 25001 carries operator-supplied period adjustments
            // (recapture, prior-period corrections) routed through
            // TaxReturn — signed.
            new("25001", "Sales Tax Adjustments", "liability",
                DetailType: "tax",
                SystemKey: "tax:adjustments",
                SystemRole: "tax_adjustments"),
            // 25002 absorbs the net-payable side of a filed return,
            // becoming the row a Pay Bills against the regulator
            // discharges later.
            new("25002", "Sales Tax Filing Liability", "liability",
                DetailType: "tax",
                SystemKey: "tax:filing_liability",
                SystemRole: "tax_filing_liability"),
            new("25500", "Income Tax Payable", "liability", DetailType: "tax"),
            new("26000", "Payroll Liabilities", "liability", DetailType: "payroll_liability"),

            // ---- Long-Term Debt (28000) ----
            new("28000", "Long-Term Debt", "liability", DetailType: "long_term_debt"),

            // ---- Equity (30000-32000) ----
            new("30000", "Opening Balance Equity", "equity",
                DetailType: "opening_balance",
                SystemKey: "equity:opening_balance"),
            new("30100", "Capital Stock", "equity", DetailType: "capital_stock"),
            new("30200", "Dividends Declared", "equity", DetailType: "dividends"),
            new("30800", "Owner's Draw", "equity", DetailType: "owners_draw"),
            new("32000", "Retained Earnings", "equity",
                DetailType: "retained_earnings",
                AllowManualPosting: false,
                SystemKey: "equity:retained_earnings",
                SystemRole: "retained_earnings"),

            // ---- Income (47900-49900) ----
            new("47900", "Sales Revenue", "revenue", DetailType: "sales"),
            new("48000", "Service Revenue", "revenue", DetailType: "service_revenue"),
            new("49900", "Uncategorized Income", "revenue", DetailType: "uncategorized"),

            // ---- Cost of Goods Sold (51000-51200) ----
            new("51000", "Cost of Goods Sold", "cost_of_sales", DetailType: "cogs"),
            new("51200", "Freight Costs", "cost_of_sales", DetailType: "freight"),

            // ---- Operating Expense (60000-69800) ----
            new("60000", "Advertising and Promotion", "expense", DetailType: "advertising"),
            new("60100", "Auto and Truck Expenses", "expense", DetailType: "auto"),
            new("60400", "Bank Service Charges", "expense", DetailType: "bank_fees"),
            new("61700", "Computer and Internet Expenses", "expense", DetailType: "technology"),
            new("62400", "Depreciation Expense", "expense", DetailType: "depreciation"),
            new("63300", "Insurance Expense", "expense", DetailType: "insurance"),
            new("63400", "Interest Expense", "expense", DetailType: "interest"),
            new("64300", "Meals and Entertainment", "expense", DetailType: "meals"),
            new("64500", "Bad Debt Expense", "expense", DetailType: "bad_debt"),
            new("64900", "Office Supplies", "expense", DetailType: "office"),
            new("65000", "Wages and Salaries", "expense", DetailType: "payroll"),
            new("65100", "Employee Benefits", "expense", DetailType: "payroll"),
            new("65500", "Professional Development", "expense", DetailType: "training"),
            new("66600", "Printing and Reproduction", "expense", DetailType: "printing"),
            new("66700", "Professional Fees", "expense", DetailType: "professional_fees"),
            new("67100", "Rent Expense", "expense", DetailType: "rent"),
            new("67200", "Repairs and Maintenance", "expense", DetailType: "repairs"),
            new("68000", "Taxes & Licenses", "expense", DetailType: "taxes_licenses"),
            new("68100", "Telephone Expense", "expense", DetailType: "telephone"),
            new("68400", "Travel Expense", "expense", DetailType: "travel"),
            new("68600", "Utilities", "expense", DetailType: "utilities"),
            new("69800", "Uncategorized Expenses", "expense", DetailType: "uncategorized"),

            // ---- FX family (77000 visible + 77100-77400 hidden system) ----
            new("77000", "Exchange Gain or Loss", "expense",
                DetailType: "fx",
                SystemKey: "fx:realized_gain",
                SystemRole: "realized_fx_gain"),
            new("77100", "Realized FX Loss", "expense",
                DetailType: "fx",
                AllowManualPosting: false,
                SystemKey: "fx:realized_loss",
                SystemRole: "realized_fx_loss"),
            new("77200", "Unrealized FX Gain", "revenue",
                DetailType: "fx",
                AllowManualPosting: false,
                SystemKey: "fx:unrealized_gain",
                SystemRole: "unrealized_fx_gain"),
            new("77300", "Unrealized FX Loss", "expense",
                DetailType: "fx",
                AllowManualPosting: false,
                SystemKey: "fx:unrealized_loss",
                SystemRole: "unrealized_fx_loss"),
            new("77400", "Translation Adjustment Reserve", "equity",
                DetailType: "fx",
                AllowManualPosting: false,
                SystemKey: "fx:translation_adjustment",
                SystemRole: "translation_adjustment"),

            // ---- Other Expense (78000-80000) ----
            new("78000", "Gain (Loss) on Sale of Assets", "expense", DetailType: "asset_disposal"),
            new("80000", "Ask My Accountant", "expense", DetailType: "other_expense"),

            // ---- Income Tax (90000) ----
            new("90000", "Income Tax Expense", "expense", DetailType: "income_tax"),
        });
}
