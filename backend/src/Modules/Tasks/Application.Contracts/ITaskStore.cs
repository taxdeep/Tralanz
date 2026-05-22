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

    /// <summary>
    /// Batch display-label resolver for known task ids — used by edit
    /// pages (bill / expense / credit-memo) to render the per-line
    /// TaskPicker with the real "TSK-000123 — Title" label instead of
    /// a placeholder short-GUID. Unknown ids are silently dropped from
    /// the result (operator sees the empty picker as if no attribution
    /// was persisted, which is the safest fallback).
    /// </summary>
    Task<IReadOnlyList<TaskDisplayLookup>> LookupDisplayAsync(
        CompanyId companyId,
        IReadOnlyList<Guid> taskIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// H6-2: stamp a single <c>task_lines</c> row with the
    /// (sourceType, sourceId, sourceLineId) tuple that just billed it.
    /// Idempotent on a same-source re-call (returns
    /// <see cref="TaskLineBillingStampOutcome.WasNewlyStamped"/> = false
    /// without raising). A re-call for the SAME line by a DIFFERENT
    /// source raises — the coordinator's pre-flight should have caught
    /// that, so this is a serious data-integrity signal, not a
    /// recoverable case.
    /// </summary>
    Task<TaskLineBillingStampOutcome> MarkLineBilledAsync(
        CompanyId companyId,
        Guid taskLineId,
        string sourceType,
        Guid sourceId,
        Guid? sourceLineId,
        DateTimeOffset billedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// H6-2: snapshot used by the workflow's status-recompute step.
    /// Returns counts of total lines vs billed lines (and the task
    /// header's current status + customer id) so the workflow can
    /// decide whether the header should flip to
    /// <see cref="TaskStatus.PartiallyBilled"/>,
    /// <see cref="TaskStatus.Billed"/>, or stay where it is. Returns
    /// null when the task does not exist in the active company.
    /// </summary>
    Task<TaskLineBillingSnapshot?> ReadLineBillingSnapshotAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Minimal display label pair returned by
/// <see cref="ITaskStore.LookupDisplayAsync"/>. Stays separate from
/// <see cref="TaskSummary"/> so the lookup path doesn't have to load
/// columns it doesn't need (currency, dates, totals).
/// </summary>
public sealed record class TaskDisplayLookup(Guid TaskId, string TaskNo, string Title);

/// <summary>
/// Outcome of <see cref="ITaskStore.MarkLineBilledAsync"/>. When the
/// line was already stamped by the same source, the call is idempotent
/// and <see cref="WasNewlyStamped"/> is false; the existing stamp's
/// source identity is returned for the caller's audit. When the
/// existing stamp identifies a DIFFERENT source the store throws —
/// that scenario indicates the coordinator's pre-flight check missed
/// a concurrent bill and must surface loudly.
/// </summary>
public sealed record class TaskLineBillingStampOutcome(
    bool WasNewlyStamped,
    string SourceType,
    Guid SourceId);

/// <summary>
/// Per-task snapshot the workflow's recompute step reads to choose the
/// header's next status. <see cref="BilledLineCount"/> counts rows with
/// <c>billed_source_id IS NOT NULL</c>; <see cref="TotalLineCount"/>
/// is the unconditional count. Status transition rule:
///   * billed == total > 0 → Billed
///   * 0 &lt; billed &lt; total → PartiallyBilled
///   * billed == 0 → keep current (caller decides)
/// </summary>
public sealed record class TaskLineBillingSnapshot(
    Guid TaskId,
    Guid? CustomerId,
    TaskStatus CurrentStatus,
    int TotalLineCount,
    int BilledLineCount);
