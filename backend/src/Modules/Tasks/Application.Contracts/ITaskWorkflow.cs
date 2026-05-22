using Citus.Modules.Tasks.Domain.Shared;

namespace Citus.Modules.Tasks.Application.Contracts;

public interface ITaskWorkflow
{
    Task<TaskRecord?> GetAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskSummary>> ListAsync(
        TaskQuery query,
        CancellationToken cancellationToken);

    Task<TaskRecord> CreateAsync(
        CompanyId companyId,
        UserId actorUserId,
        TaskCreateRequest request,
        CancellationToken cancellationToken);

    Task<TaskRecord> UpdateAsync(
        CompanyId companyId,
        Guid taskId,
        UserId actorUserId,
        TaskUpdateRequest request,
        CancellationToken cancellationToken);

    Task<TaskRecord> AddLineAsync(
        CompanyId companyId,
        Guid taskId,
        UserId actorUserId,
        TaskLineUpsertRequest request,
        CancellationToken cancellationToken);

    Task<TaskRecord> RemoveLineAsync(
        CompanyId companyId,
        Guid taskId,
        Guid lineId,
        UserId actorUserId,
        CancellationToken cancellationToken);

    /// <summary>Open → Completed.</summary>
    Task<TaskRecord> CompleteAsync(
        CompanyId companyId,
        Guid taskId,
        UserId actorUserId,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>Open or Completed → Canceled. Terminal.</summary>
    Task<TaskRecord> CancelAsync(
        CompanyId companyId,
        Guid taskId,
        UserId actorUserId,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Completed → Billed. Called by AR after a posted invoice
    /// captures these tasks. Not exposed as a public API endpoint
    /// — used by Batch 9's <c>InvoicePosted</c> domain event
    /// handler.
    /// </summary>
    Task<TaskRecord> MarkBilledAsync(
        CompanyId companyId,
        Guid taskId,
        Guid invoiceId,
        UserId actorUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Billed → Completed. Rollback companion to
    /// <see cref="MarkBilledAsync"/>; invoked when AR voids the
    /// invoice that captured this task.
    /// </summary>
    Task<TaskRecord> RestoreFromBilledAsync(
        CompanyId companyId,
        Guid taskId,
        UserId actorUserId,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// H6-2: after the coordinator has stamped one or more
    /// <c>task_lines</c> billed for a given source, recompute the
    /// header status from the line-level state and transition if
    /// needed. The rules (D2/D3 confirmed):
    ///   * billed == total > 0          → Billed
    ///   * 0 &lt; billed &lt; total    → PartiallyBilled
    ///   * billed == 0                   → no transition (caller is
    ///                                     responsible for the prior
    ///                                     state, e.g. rollback path)
    /// Pass-through when the resulting status matches the current
    /// header status. Returns the (possibly unchanged) task record.
    /// </summary>
    Task<TaskRecord> RecomputeAndTransitionFromLinesAsync(
        CompanyId companyId,
        Guid taskId,
        string sourceType,
        Guid sourceId,
        UserId actorUserId,
        CancellationToken cancellationToken);
}
