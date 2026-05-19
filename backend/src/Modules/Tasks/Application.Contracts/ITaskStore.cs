using Citus.Modules.Tasks.Domain.Shared;
// Disambiguate the type name against System.Threading.Tasks.TaskStatus,
// which the implicit-usings facade pulls in everywhere.
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.Modules.Tasks.Application.Contracts;

public interface ITaskStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<TaskRecord?> GetAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskSummary>> ListAsync(
        TaskQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a header row with status=open and a freshly minted
    /// task_no allocated from the per-company sequence.
    /// </summary>
    Task<TaskRecord> CreateAsync(
        CompanyId companyId,
        UserId createdBy,
        string title,
        string? description,
        Guid? customerId,
        Guid? projectId,
        UserId? assignedToUserId,
        DateOnly? serviceDate,
        string currencyCode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces header columns. Caller (workflow) has already
    /// verified the task is in <see cref="TaskStatus.Open"/>.
    /// </summary>
    Task<TaskRecord?> UpdateHeaderAsync(
        CompanyId companyId,
        Guid taskId,
        string title,
        string? description,
        Guid? customerId,
        Guid? projectId,
        UserId? assignedToUserId,
        DateOnly? serviceDate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends a line, recomputes the header total, and returns the
    /// updated task. Throws if the task is not open or if a line
    /// with the given line_no would collide.
    /// </summary>
    Task<TaskRecord> AppendLineAsync(
        CompanyId companyId,
        Guid taskId,
        Guid itemId,
        string? description,
        decimal quantity,
        decimal unitPrice,
        string currencyCode,
        Guid? taxCodeId,
        CancellationToken cancellationToken);

    Task<TaskRecord> RemoveLineAsync(
        CompanyId companyId,
        Guid taskId,
        Guid lineId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomic state transition. Writes the task row and an
    /// audit row in the same transaction.
    /// </summary>
    Task<TaskRecord?> TransitionStatusAsync(
        CompanyId companyId,
        Guid taskId,
        TaskStatus fromStatus,
        TaskStatus toStatus,
        UserId actorUserId,
        string? reason,
        Guid? billedInvoiceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskStateTransitionRecord>> ListTransitionsAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reverse lookup for the billing-rollback path: returns every
    /// task in the company whose <c>billed_invoice_id</c> equals
    /// <paramref name="invoiceId"/>. Order is undefined; callers
    /// process the set as a batch.
    /// </summary>
    Task<IReadOnlyList<TaskSummary>> ListByBilledInvoiceAsync(
        CompanyId companyId,
        Guid invoiceId,
        CancellationToken cancellationToken);
}
