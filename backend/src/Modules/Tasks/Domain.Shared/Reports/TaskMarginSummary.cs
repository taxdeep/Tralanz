namespace Citus.Modules.Tasks.Domain.Shared.Reports;

/// <summary>
/// Roll-up across every row in the result set (post-filter, NOT
/// page-limited — clamp/skip on <see cref="TaskMarginReportQuery"/>
/// only narrow which rows are returned; the summary always reflects
/// the full filtered set). <see cref="WeightedGrossMarginPercent"/>
/// uses <c>SUM(margin) / SUM(billable)</c> rather than the average of
/// per-row percentages, so a single big task doesn't get drowned out
/// by lots of small ones.
/// </summary>
public sealed record class TaskMarginSummary
{
    public required int TaskCount { get; init; }

    public required decimal TotalBillableValue { get; init; }

    public required decimal TotalDirectCost { get; init; }

    public required decimal TotalGrossMargin { get; init; }

    public decimal? WeightedGrossMarginPercent { get; init; }

    /// <summary>
    /// Company base currency the *Base sums are denominated in. Matches
    /// every row's BaseCurrencyCode.
    /// </summary>
    public required string BaseCurrencyCode { get; init; }

    /// <summary>
    /// Sum of each row's <c>BillableValueBase</c>. Honest single-currency
    /// figure suitable for headline display, unlike
    /// <see cref="TotalBillableValue"/> which sums raw amounts across
    /// possibly-mixed currencies.
    /// </summary>
    public required decimal TotalBillableValueBase { get; init; }

    /// <summary>Sum of each row's <c>DirectCostBase</c>.</summary>
    public required decimal TotalDirectCostBase { get; init; }

    /// <summary>Sum of each row's <c>GrossMarginBase</c>.</summary>
    public required decimal TotalGrossMarginBase { get; init; }

    /// <summary>
    /// Base-currency weighted gross-margin percent —
    /// <c>TotalGrossMarginBase / TotalBillableValueBase × 100</c>.
    /// Null when <see cref="TotalBillableValueBase"/> is zero.
    /// </summary>
    public decimal? WeightedGrossMarginPercentBase { get; init; }

    /// <summary>
    /// Number of rows where no FX rate was found and the service fell
    /// back to <c>FxRate=1</c>. When &gt; 0 the UI should display a
    /// caveat — the base-currency totals understate / overstate by the
    /// missing-rate slice.
    /// </summary>
    public required int UnresolvedFxCount { get; init; }
}
