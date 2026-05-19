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
}
