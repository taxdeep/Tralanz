namespace Citus.Modules.Tasks.Domain.Shared.Reports;

/// <summary>
/// Selects which slice of the task book a margin report shows.
/// </summary>
public enum TaskMarginReportMode
{
    /// <summary>
    /// Every non-cancelled task (open, completed, billed). Answers
    /// "are we profitable on the work we're currently doing?" Includes
    /// in-flight tasks that have direct cost accruing but no revenue
    /// recognised yet.
    /// </summary>
    Operational = 0,

    /// <summary>
    /// Only tasks in <see cref="TaskStatus.Billed"/>. Answers "what
    /// margin have we actually realised from billed work?" Excludes
    /// in-flight work — the denominator is realised revenue only.
    /// </summary>
    Billed = 1,
}
