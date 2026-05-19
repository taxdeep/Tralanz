using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.Modules.Tasks.Application;

public sealed class TaskLineLinkValidator(ITaskStore store) : ITaskLineLinkValidator
{
    public async Task ValidateAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to link a line to a task.");
        }

        if (taskId == Guid.Empty)
        {
            throw new InvalidOperationException("Task id is required to link a line.");
        }

        var task = await store.GetAsync(companyId, taskId, cancellationToken);
        if (task is null)
        {
            // GetAsync already scopes by company, so a null result
            // means "no row in this company". Surface a single
            // friendly message regardless of cause — the audit log
            // will carry the actor + the rejected GUID for forensic
            // detail if it ever matters.
            throw new InvalidOperationException(
                $"Task '{taskId:D}' was not found in the active company. Cross-company task links are not permitted.");
        }

        if (task.Status is not (TaskStatus.Open or TaskStatus.Completed))
        {
            throw new InvalidOperationException(
                $"Task '{task.TaskNo}' is in status '{task.Status.ToToken()}' and cannot accept new line attribution. " +
                "Only open or completed tasks may receive AR/AP line links.");
        }
    }
}
