using Citus.Modules.Tasks.Domain.Shared;

namespace Citus.Modules.Tasks.Application.Contracts;

/// <summary>
/// Read-model rollup: every AR / AP document that has at least one
/// line linked to a given task. Powers the TaskDetailPage "Related
/// documents" section so the operator can see, in one place, what
/// revenue (invoices / credit notes) and cost (bills / expenses)
/// the task has accumulated — without bouncing to the margin report.
///
/// Always scoped by company and task id; cross-company links are
/// physically impossible because the underlying *_lines tables all
/// carry company_id (or join through their parent which does).
/// </summary>
public interface ITaskRelatedDocumentsService
{
    Task<IReadOnlyList<TaskRelatedDocument>> ListForTaskAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken);
}
