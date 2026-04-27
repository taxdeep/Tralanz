using Citus.Accounting.Application.Abstractions;

namespace Citus.Accounting.Application.CoaTemplates;

/// <summary>
/// In-process registry of starter CoA templates. New templates are added
/// by appending to <see cref="BuildTemplates"/>. Each template's
/// <see cref="CoaTemplate.Version"/> bumps when its account list
/// changes so audit logs can trace which content was applied.
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
            BuildGeneral4Digit(),
        };

    /// <summary>
    /// Generic 4-digit starter chart. Covers all six root types and
    /// names the four control accounts the Posting Engine resolves by
    /// system role:
    ///   * accounts_receivable (1200)
    ///   * accounts_payable    (2000)
    ///   * retained_earnings   (3100)
    ///   * fx_revaluation      (5600)
    /// Reserved code ranges follow the multi-currency provisioning plan
    /// (1210–1249 for AR sub-currency, 3010–3049 for AP sub-currency,
    /// 5600–5699 for FX) so future seeders can fit alongside without
    /// renumbering.
    /// </summary>
    private static CoaTemplate BuildGeneral4Digit() => new(
        Key: "general_4digit",
        Version: "1",
        Name: "General business (4-digit codes)",
        Description: "Common starter chart: cash, AR/AP control, sales, COGS, payroll-style expense buckets, and FX revaluation. Works for most service or trade businesses; add industry-specific accounts after seeding.",
        Country: "Generic",
        AccountCodeLength: 4,
        Accounts: new CoaTemplateAccount[]
        {
            // ----- Asset --------------------------------------------------
            new("1000", "Cash", "asset", DetailType: "bank"),
            new("1100", "Petty Cash", "asset", DetailType: "bank"),
            new("1110", "Bank Checking", "asset", DetailType: "bank"),
            new("1200", "Accounts Receivable", "asset",
                DetailType: "receivable",
                SystemKey: "accounts_receivable",
                SystemRole: "accounts_receivable"),
            new("1500", "Inventory", "asset", DetailType: "inventory"),
            new("1600", "Equipment", "asset", DetailType: "fixed_asset"),
            new("1610", "Accumulated Depreciation - Equipment", "asset", DetailType: "contra_asset"),

            // ----- Liability ----------------------------------------------
            new("2000", "Accounts Payable", "liability",
                DetailType: "payable",
                SystemKey: "accounts_payable",
                SystemRole: "accounts_payable"),
            new("2200", "Sales Tax Payable", "liability", DetailType: "tax"),
            new("2400", "Notes Payable", "liability", DetailType: "long_term_debt"),

            // ----- Equity -------------------------------------------------
            new("3000", "Owner's Equity", "equity", DetailType: "equity"),
            new("3100", "Retained Earnings", "equity",
                DetailType: "retained_earnings",
                AllowManualPosting: false,
                SystemKey: "retained_earnings",
                SystemRole: "retained_earnings"),

            // ----- Revenue ------------------------------------------------
            new("4000", "Sales Revenue", "revenue", DetailType: "sales"),
            new("4500", "Service Revenue", "revenue", DetailType: "service"),

            // ----- Cost of sales ------------------------------------------
            new("5000", "Cost of Goods Sold", "cost_of_sales", DetailType: "inventory"),

            // ----- Expense ------------------------------------------------
            new("5600", "Foreign Exchange Gain / Loss", "expense",
                DetailType: "fx",
                SystemKey: "fx_revaluation",
                SystemRole: "fx_revaluation"),
            new("6000", "Office Expense", "expense", DetailType: "office"),
            new("6100", "Rent Expense", "expense", DetailType: "rent"),
            new("6200", "Utilities Expense", "expense", DetailType: "utilities"),
            new("6300", "Salaries Expense", "expense", DetailType: "payroll"),
            new("6400", "Insurance Expense", "expense", DetailType: "insurance"),
            new("6500", "Marketing Expense", "expense", DetailType: "marketing"),
            new("6900", "Bank Charges", "expense", DetailType: "bank_fees"),
        });
}
