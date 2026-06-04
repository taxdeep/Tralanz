namespace Citus.Business.Blazor.Components.Features.Reports;

/// <summary>
/// One entry in the reports hub. Hub + viewer are both driven from this
/// catalog, so adding a report is one row here — not a copy/pasted page.
/// <c>IsImplemented=false</c> shows the row greyed-out ("coming soon").
/// </summary>
public sealed record ReportDefinition(
    string Key,
    string Title,
    string Category,
    string Description,
    bool IsImplemented = true);

public static class ReportCatalog
{
    public const string BusinessOverview = "Business overview";
    public const string WhoOwesYou = "Who owes you";
    public const string WhatYouOwe = "What you owe";

    // Hub renders categories in this order; empty categories are skipped.
    private static readonly string[] CategoryOrder =
    {
        BusinessOverview,
        WhoOwesYou,
        WhatYouOwe,
    };

    public static readonly IReadOnlyList<ReportDefinition> All = new[]
    {
        new ReportDefinition("profit-and-loss", "Profit and Loss", BusinessOverview,
            "Revenue, cost of sales, expenses, and net income across a date range."),
        new ReportDefinition("balance-sheet", "Balance Sheet", BusinessOverview,
            "Assets, liabilities, and equity as of a cut-off date."),
        new ReportDefinition("trial-balance", "Trial Balance", BusinessOverview,
            "Debit and credit balance per account as of a date."),
        new ReportDefinition("ar-aging-summary", "A/R Aging Summary", WhoOwesYou,
            "Customer receivables grouped by aging bucket."),
        new ReportDefinition("ar-aging-detail", "A/R Aging Detail", WhoOwesYou,
            "Every open receivable listed under its customer, with per-document aging."),
        new ReportDefinition("customer-statement", "Customer Statement", WhoOwesYou,
            "One customer's open items and balance as of a date."),
        new ReportDefinition("ap-aging-summary", "A/P Aging Summary", WhatYouOwe,
            "Vendor payables grouped by aging bucket."),
        new ReportDefinition("ap-aging-detail", "A/P Aging Detail", WhatYouOwe,
            "Every open payable listed under its vendor, with per-document aging."),
        new ReportDefinition("vendor-statement", "Vendor Statement", WhatYouOwe,
            "One vendor's open items and balance as of a date."),
    };

    public static ReportDefinition? ByKey(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<(string Category, IReadOnlyList<ReportDefinition> Reports)> ByCategory() =>
        CategoryOrder
            .Select(category => (
                Category: category,
                Reports: (IReadOnlyList<ReportDefinition>)All.Where(r => r.Category == category).ToList()))
            .Where(group => group.Reports.Count > 0)
            .ToList();
}
