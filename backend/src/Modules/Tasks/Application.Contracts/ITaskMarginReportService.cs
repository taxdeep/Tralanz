using Citus.Modules.Tasks.Domain.Shared.Reports;

namespace Citus.Modules.Tasks.Application.Contracts;

/// <summary>
/// Read-model service: aggregates per-task revenue vs cost into the
/// margin report. Lives in the Task module because the canonical
/// definition of "what counts as a task's direct cost" belongs here,
/// not in AP — even though the underlying numbers come from
/// <c>bill_lines</c> and <c>expense_lines</c>.
/// </summary>
public interface ITaskMarginReportService
{
    Task<TaskMarginReportResult> GetReportAsync(
        TaskMarginReportQuery query,
        CancellationToken cancellationToken);
}
