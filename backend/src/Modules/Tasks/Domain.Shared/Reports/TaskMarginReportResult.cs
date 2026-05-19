namespace Citus.Modules.Tasks.Domain.Shared.Reports;

/// <summary>
/// What the report endpoint returns: paged rows plus a summary that
/// always reflects the unpaged filtered set. Currency mixing is
/// possible across rows (each task carries its own currency); the
/// summary intentionally sums raw numbers without FX conversion — UI
/// must surface a "multi-currency" badge when rows differ. v1 ships
/// without FX consolidation; a later batch can layer it on.
/// </summary>
public sealed record class TaskMarginReportResult
{
    public required TaskMarginReportMode Mode { get; init; }

    public required IReadOnlyList<TaskMarginRow> Rows { get; init; }

    public required TaskMarginSummary Summary { get; init; }
}
