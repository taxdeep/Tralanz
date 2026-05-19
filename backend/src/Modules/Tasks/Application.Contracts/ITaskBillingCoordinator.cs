using Citus.Modules.Tasks.Domain.Shared;

namespace Citus.Modules.Tasks.Application.Contracts;

/// <summary>
/// Wires AR's "an invoice posted" / "an invoice voided" signal into
/// the Task module's billing state. This is the canonical Task-side
/// entry point — AR (or a UI orchestrator) calls these methods at the
/// right moment; the coordinator handles validation, multi-task
/// fan-out, idempotency, and rollback.
///
/// The coordinator does NOT build or post AR documents — that path
/// stays with <c>PostInvoiceCommandHandler</c> and the existing
/// <c>/accounting/invoices/drafts</c> + <c>/post</c> surface. This
/// service is the Task-side bookkeeping companion: once AR has
/// successfully posted (or voided) an invoice carrying task lines,
/// the orchestrator invokes us to flip every affected task's
/// <c>completed → billed</c> (or <c>billed → completed</c>) flag.
/// </summary>
public interface ITaskBillingCoordinator
{
    /// <summary>
    /// Marks every task in <paramref name="taskIds"/> as billed by the
    /// given AR invoice. Validates that each task belongs to the
    /// active company, is currently in <c>Completed</c> status, and
    /// (when <paramref name="customerId"/> is non-null) matches that
    /// customer. Tasks already billed by the same invoice are skipped
    /// (idempotent). Tasks billed by a different invoice raise an
    /// error — that case indicates a serious bookkeeping drift that
    /// must not be silently overwritten.
    /// </summary>
    Task<TaskBillingResult> MarkAsBilledAsync(
        CompanyId companyId,
        Guid invoiceId,
        Guid? customerId,
        IReadOnlyList<Guid> taskIds,
        UserId actorUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds every task whose <c>billed_invoice_id</c> equals
    /// <paramref name="invoiceId"/> and restores it to
    /// <c>Completed</c>. Tasks already restored are skipped
    /// (idempotent re-call after a partial failure is safe). Used by
    /// the AR void path and any manual unbill.
    /// </summary>
    Task<TaskBillingResult> RollbackBillingAsync(
        CompanyId companyId,
        Guid invoiceId,
        UserId actorUserId,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Precise per-task rollback: for each id in <paramref name="taskIds"/>,
    /// re-reads the task and restores Billed -> Completed if it is
    /// currently in <see cref="TaskStatus.Billed"/>. Tasks in any other
    /// status are skipped (idempotent). Unlike
    /// <see cref="RollbackBillingAsync"/> this does NOT take an invoice
    /// id and does not bulk-rollback by invoice — the caller specifies
    /// exactly which tasks to release. Used by the credit-note post
    /// hook so a partial credit only releases the tasks the credit
    /// actually reverses.
    /// </summary>
    Task<TaskBillingResult> RollbackByTaskIdsAsync(
        CompanyId companyId,
        IReadOnlyList<Guid> taskIds,
        UserId actorUserId,
        string? reason,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-call summary the coordinator returns. <see cref="ProcessedTasks"/>
/// is the set the operation actually transitioned;
/// <see cref="SkippedTasks"/> is the set that was already in the
/// target state (idempotent no-op).
/// </summary>
public sealed record class TaskBillingResult
{
    public required Guid InvoiceId { get; init; }

    public required IReadOnlyList<TaskSummary> ProcessedTasks { get; init; }

    public required IReadOnlyList<TaskSummary> SkippedTasks { get; init; }
}
