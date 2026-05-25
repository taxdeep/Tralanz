namespace Citus.Business.Blazor.Components.Features.ChartOfAccounts;

/// <summary>
/// System-defined detail-type catalog, grouped by root type. Values
/// mirror the strings used by <c>StaticCoaTemplateRegistry</c>'s seed
/// templates (so a custom-created account picks up the same posting /
/// reporting routing as a templated one with the same detail type).
///
/// Extracted in Batch B so both <c>ChartOfAccountsPage</c>'s full form
/// and <c>AccountPicker</c>'s inline quick-create panel can populate
/// their Detail-type dropdowns from the same source — the previous
/// inline-only constant was a duplication risk waiting to happen.
/// </summary>
public static class AccountDetailTypeCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Value, string Label)>> _catalog =
        new Dictionary<string, IReadOnlyList<(string Value, string Label)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["asset"] = new (string, string)[]
            {
                ("cash", "Cash"),
                ("bank", "Bank"),
                ("accounts_receivable", "Accounts receivable"),
                ("undeposited_funds", "Undeposited funds"),
                ("short_term_investments", "Short-term investments"),
                ("employee_advances", "Employee advances"),
                ("prepaids", "Prepaids"),
                ("inventory", "Inventory"),
                ("fixed_asset", "Fixed asset"),
                ("contra_asset", "Contra asset"),
                ("other_asset", "Other asset"),
            },
            ["liability"] = new (string, string)[]
            {
                ("accounts_payable", "Accounts payable"),
                ("credit_card", "Credit card"),
                ("shareholder_loan", "Shareholder loan"),
                ("customer_deposits", "Customer deposits"),
                ("tax", "Tax"),
                ("payroll_liability", "Payroll liability"),
                ("long_term_debt", "Long-term debt"),
            },
            ["equity"] = new (string, string)[]
            {
                ("opening_balance", "Opening balance"),
                ("capital_stock", "Capital stock"),
                ("dividends", "Dividends"),
                ("owners_draw", "Owner's draw"),
                ("retained_earnings", "Retained earnings"),
            },
            ["revenue"] = new (string, string)[]
            {
                ("sales", "Sales"),
                ("service_revenue", "Service revenue"),
                ("uncategorized", "Uncategorized"),
            },
            ["cost_of_sales"] = new (string, string)[]
            {
                ("cogs", "COGS"),
                ("freight", "Freight"),
            },
            ["expense"] = new (string, string)[]
            {
                ("advertising", "Advertising"),
                ("auto", "Auto"),
                ("bad_debt", "Bad debt"),
                ("bank_fees", "Bank fees"),
                ("depreciation", "Depreciation"),
                ("insurance", "Insurance"),
                ("interest", "Interest"),
                ("meals", "Meals & entertainment"),
                ("office", "Office"),
                ("payroll", "Payroll"),
                ("printing", "Printing"),
                ("professional_fees", "Professional fees"),
                ("rent", "Rent"),
                ("repairs", "Repairs"),
                ("taxes_licenses", "Taxes & licenses"),
                ("technology", "Technology"),
                ("telephone", "Telephone"),
                ("training", "Training"),
                ("travel", "Travel"),
                ("utilities", "Utilities"),
                ("uncategorized", "Uncategorized"),
                ("asset_disposal", "Asset disposal"),
                ("other_expense", "Other expense"),
                ("income_tax", "Income tax"),
                ("fx", "FX gain / loss"),
            },
        };

    /// <summary>
    /// Returns the (value, human-label) tuples for the given root type.
    /// Unknown root → empty list (so callers can render "—" gracefully
    /// without throwing).
    /// </summary>
    public static IReadOnlyList<(string Value, string Label)> OptionsFor(string rootType) =>
        _catalog.TryGetValue(rootType, out var options)
            ? options
            : Array.Empty<(string, string)>();
}
