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
    /// Canonical 5-digit starter chart, derived from the user-supplied
    /// "QuickBooks Online" reference (account list.pdf, 2026-04-27).
    /// 64 user-visible accounts plus 4 hidden FX system rows so the
    /// Posting Engine has distinct accounts for realized loss /
    /// unrealized gain / unrealized loss / translation adjustment even
    /// when the income statement face shows the single "Exchange Gain
    /// or Loss" row.
    ///
    /// All canonical codes are exactly 5 digits. The Wizard's account
    /// code length picker (4–10) reformats them on apply: trailing zeros
    /// are dropped for shorter lengths and appended for longer ones, so
    /// 10000 Cash on Hand becomes 1000 at length 4, 100000 at length 6,
    /// 1000000 at length 7, etc.
    /// </summary>
    private static CoaTemplate BuildCanonical5Digit() => new(
        Key: "ca_general_small_business",
        Version: "2",
        Name: "General business (5-digit canonical)",
        Description: "Default chart of accounts seeded by the First-Company Wizard. 64 visible accounts covering bank, AR/AP control, current/fixed/other assets, current liabilities, equity, income, COGS, operating expense, and FX, plus engine-required hidden FX system rows (realized loss / unrealized gain / unrealized loss / translation adjustment). Codes are stored at 5 digits and shifted to the company's chosen length (4–10).",
        Country: "Generic",
        AccountCodeLength: 5,
        Accounts: new CoaTemplateAccount[]
        {
            // ---- Banks (10000-10999) ----
            new("10000", "Cash on Hand", "asset", DetailType: "bank"),
            new("10010", "JP CAD Chequing", "asset", DetailType: "bank"),
            new("10020", "JPM-USD-4419", "asset", DetailType: "bank"),
            new("10999", "CLEARING ACCT-USD", "asset", DetailType: "clearing"),

            // ---- Accounts Receivable (11000 base + 11001 USD subcurrency) ----
            new("11000", "Accounts Receivable", "asset",
                DetailType: "accounts_receivable",
                AllowManualPosting: false,
                SystemKey: "control_account:accounts_receivable:base",
                SystemRole: "accounts_receivable"),
            new("11001", "Accounts Receivable - USD", "asset",
                DetailType: "accounts_receivable_subcurrency",
                AllowManualPosting: false,
                SystemKey: "control_account:accounts_receivable:usd"),

            // ---- Other Current Asset ----
            new("12000", "Undeposited Funds", "asset", DetailType: "undeposited_funds"),
            new("12800", "Employee Advances", "asset", DetailType: "employee_advances"),
            new("13100", "Prepaid Insurance", "asset", DetailType: "prepaids"),
            new("18100", "Inventory", "asset", DetailType: "inventory"),

            // ---- Fixed Asset ----
            new("15000", "Furniture and Equipment", "asset", DetailType: "fixed_asset"),
            new("15200", "Buildings and Improvements", "asset", DetailType: "fixed_asset"),
            new("15400", "Custom Software", "asset", DetailType: "fixed_asset"),
            new("15600", "Land", "asset", DetailType: "fixed_asset"),
            new("15900", "Leasehold Improvements", "asset", DetailType: "fixed_asset"),
            new("16300", "Tractors and Trailers", "asset", DetailType: "fixed_asset"),
            new("16400", "Vehicles", "asset", DetailType: "fixed_asset"),
            new("16500", "Warehouse Equipment", "asset", DetailType: "fixed_asset"),
            new("17000", "Accumulated Depreciation", "asset", DetailType: "contra_asset"),

            // ---- Other Asset ----
            new("18010", "Due from Affiliates", "asset", DetailType: "other_asset"),
            new("18700", "Security Deposits Asset", "asset", DetailType: "other_asset"),

            // ---- Accounts Payable (20000 base + 20001 USD subcurrency) ----
            new("20000", "Accounts Payable", "liability",
                DetailType: "accounts_payable",
                AllowManualPosting: false,
                SystemKey: "control_account:accounts_payable:base",
                SystemRole: "accounts_payable"),
            new("20001", "Accounts Payable - USD", "liability",
                DetailType: "accounts_payable_subcurrency",
                AllowManualPosting: false,
                SystemKey: "control_account:accounts_payable:usd"),

            // ---- Other Current Liability ----
            new("24000", "Shareholder Loan", "liability", DetailType: "shareholder_loan"),
            new("24010", "Loan to Affiliates", "liability", DetailType: "intercompany_loan"),
            new("24700", "Customer Deposits", "liability", DetailType: "customer_deposits"),
            new("25500", "GST/HST Payable", "liability", DetailType: "tax"),
            new("25530", "GST/QST Payable", "liability", DetailType: "tax"),
            new("25550", "PST Payable (BC)", "liability", DetailType: "tax"),
            new("26100", "Worker's Comp Premiums - Admin", "liability", DetailType: "payroll_liability"),

            // ---- Equity ----
            new("30000", "Opening Balance Equity", "equity",
                DetailType: "opening_balance",
                SystemKey: "equity:opening_balance"),
            new("30100", "Capital Stock", "equity", DetailType: "capital_stock"),
            new("30200", "Dividends Paid", "equity", DetailType: "dividends"),
            new("30800", "Owners Draw", "equity", DetailType: "owners_draw"),
            new("31400", "Shareholder Distributions", "equity", DetailType: "shareholder_distributions"),
            new("32000", "Retained Earnings", "equity",
                DetailType: "retained_earnings",
                AllowManualPosting: false,
                SystemKey: "equity:retained_earnings",
                SystemRole: "retained_earnings"),

            // ---- Income ----
            new("47900", "Sales", "revenue", DetailType: "sales"),
            new("48900", "Shipping and Delivery Income", "revenue", DetailType: "shipping_revenue"),
            new("49900", "Uncategorized Income", "revenue", DetailType: "uncategorized"),

            // ---- Cost of Goods Sold ----
            new("51000", "Purchase Cost", "cost_of_sales", DetailType: "purchase_cost"),
            new("51200", "Freight Costs", "cost_of_sales", DetailType: "freight"),
            new("51800", "Merchant Account Fees", "cost_of_sales", DetailType: "merchant_fees"),

            // ---- Operating Expense ----
            new("60000", "Advertising and Promotion", "expense", DetailType: "advertising"),
            new("60100", "Auto and Truck Expenses", "expense", DetailType: "auto"),
            new("60400", "Bank Service Charges", "expense", DetailType: "bank_fees"),
            new("61700", "Computer and Internet Expenses", "expense", DetailType: "technology"),
            new("62400", "Depreciation Expense", "expense", DetailType: "depreciation"),
            new("63300", "Insurance Expense", "expense", DetailType: "insurance"),
            new("63400", "Interest Expense", "expense", DetailType: "interest"),
            new("64300", "Meals and Entertainment", "expense", DetailType: "meals"),
            new("64900", "Office Supplies", "expense", DetailType: "office"),
            new("66600", "Printing and Reproduction", "expense", DetailType: "printing"),
            new("66700", "Professional Fees", "expense", DetailType: "professional_fees"),
            new("67100", "Rent Expense", "expense", DetailType: "rent"),
            new("67200", "Repairs and Maintenance", "expense", DetailType: "repairs"),
            new("68000", "Taxes - Property", "expense", DetailType: "property_tax"),
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

            // ---- Other Expense ----
            new("80000", "Ask My Accountant", "expense", DetailType: "other_expense"),
        });
}
