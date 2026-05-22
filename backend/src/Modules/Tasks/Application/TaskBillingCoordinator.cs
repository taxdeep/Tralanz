using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.Modules.Tasks.Application;

public sealed class TaskBillingCoordinator(
    ITaskStore store,
    ITaskWorkflow workflow) : ITaskBillingCoordinator
{
    public async Task<TaskBillingResult> MarkAsBilledAsync(
        CompanyId companyId,
        Guid invoiceId,
        Guid? customerId,
        IReadOnlyList<Guid> taskIds,
        UserId actorUserId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to mark tasks billed.");
        }
        if (invoiceId == Guid.Empty)
        {
            throw new InvalidOperationException("Invoice id is required to mark tasks billed.");
        }
        if (actorUserId.Value is null)
        {
            throw new InvalidOperationException("An acting user is required to mark tasks billed.");
        }
        if (taskIds is null || taskIds.Count == 0)
        {
            throw new InvalidOperationException("At least one task id is required.");
        }

        var distinctIds = taskIds
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (distinctIds.Length == 0)
        {
            throw new InvalidOperationException("At least one non-empty task id is required.");
        }

        // First pass: read every task and pre-flight validate. We
        // refuse to start writing if any input is invalid, so the
        // post-write state is "all-or-nothing visible" from the
        // caller's perspective (even though each MarkBilledAsync is
        // its own transaction underneath).
        var tasksToProcess = new List<TaskRecord>(distinctIds.Length);
        var skipped = new List<TaskRecord>();
        foreach (var taskId in distinctIds)
        {
            var task = await store.GetAsync(companyId, taskId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Task '{taskId:D}' was not found in the active company. Cross-company billing is not permitted.");

            if (customerId.HasValue && task.CustomerId != customerId.Value)
            {
                throw new InvalidOperationException(
                    $"Task '{task.TaskNo}' is attached to a different customer than the invoice. A single invoice cannot bill tasks from mixed customers.");
            }

            switch (task.Status)
            {
                case TaskStatus.Billed when task.BilledInvoiceId == invoiceId:
                    // Idempotent re-call — already billed by THIS
                    // invoice. Surface in SkippedTasks so the caller
                    // knows nothing changed.
                    skipped.Add(task);
                    break;

                case TaskStatus.Billed:
                    // Billed by a DIFFERENT invoice. Refuse — silent
                    // overwrite would corrupt the billed-margin read
                    // model for the original invoice.
                    throw new InvalidOperationException(
                        $"Task '{task.TaskNo}' is already billed by invoice '{task.BilledInvoiceId:D}'; it cannot be re-billed by invoice '{invoiceId:D}'.");

                case TaskStatus.Completed:
                    tasksToProcess.Add(task);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Task '{task.TaskNo}' is in status '{task.Status.ToToken()}' and cannot be billed. Only completed tasks may be marked billed.");
            }
        }

        var processed = new List<TaskSummary>(tasksToProcess.Count);
        foreach (var task in tasksToProcess)
        {
            var billed = await workflow.MarkBilledAsync(companyId, task.Id, invoiceId, actorUserId, cancellationToken);
            processed.Add(ToSummary(billed));
        }

        return new TaskBillingResult
        {
            InvoiceId = invoiceId,
            ProcessedTasks = processed,
            SkippedTasks = skipped.Select(ToSummary).ToArray(),
        };
    }

    public async Task<TaskBillingResult> RollbackBillingAsync(
        CompanyId companyId,
        Guid invoiceId,
        UserId actorUserId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to roll back task billing.");
        }
        if (invoiceId == Guid.Empty)
        {
            throw new InvalidOperationException("Invoice id is required to roll back task billing.");
        }
        if (actorUserId.Value is null)
        {
            throw new InvalidOperationException("An acting user is required to roll back task billing.");
        }

        // Reverse lookup: every task this invoice billed. May be
        // empty (no tasks were linked, e.g. a manual-entry invoice),
        // in which case the result is a successful no-op.
        var linked = await store.ListByBilledInvoiceAsync(companyId, invoiceId, cancellationToken);
        if (linked.Count == 0)
        {
            return Empty(invoiceId);
        }

        var processed = new List<TaskSummary>(linked.Count);
        var skipped = new List<TaskSummary>();

        foreach (var summary in linked)
        {
            // GetAsync re-reads the row to catch out-of-band status
            // changes between the lookup and the transition.
            var task = await store.GetAsync(companyId, summary.Id, cancellationToken);
            if (task is null) continue;

            if (task.Status != TaskStatus.Billed)
            {
                // Another caller (a previous rollback retry, manual
                // intervention) already moved this row out of Billed.
                // Skip rather than throw — the goal state is reached.
                skipped.Add(ToSummary(task));
                continue;
            }

            var restored = await workflow.RestoreFromBilledAsync(
                companyId,
                task.Id,
                actorUserId,
                string.IsNullOrWhiteSpace(reason)
                    ? $"AR invoice {invoiceId:D} voided; task restored to completed."
                    : reason.Trim(),
                cancellationToken);
            processed.Add(ToSummary(restored));
        }

        return new TaskBillingResult
        {
            InvoiceId = invoiceId,
            ProcessedTasks = processed,
            SkippedTasks = skipped,
        };
    }

    public async Task<TaskBillingResult> RollbackByTaskIdsAsync(
        CompanyId companyId,
        IReadOnlyList<Guid> taskIds,
        UserId actorUserId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to roll back task billing.");
        }
        if (actorUserId.Value is null)
        {
            throw new InvalidOperationException("An acting user is required to roll back task billing.");
        }
        if (taskIds is null || taskIds.Count == 0)
        {
            return Empty(invoiceId: Guid.Empty);
        }

        var distinctIds = taskIds
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (distinctIds.Length == 0)
        {
            return Empty(invoiceId: Guid.Empty);
        }

        var processed = new List<TaskSummary>(distinctIds.Length);
        var skipped = new List<TaskSummary>();

        foreach (var taskId in distinctIds)
        {
            var task = await store.GetAsync(companyId, taskId, cancellationToken);
            // Skip silently if the task isn't in this company — the
            // caller may have asked us to roll back tasks discovered
            // on a credit-note line that references a deleted /
            // cross-company task. Don't fail the whole batch.
            if (task is null) continue;

            if (task.Status != TaskStatus.Billed)
            {
                // Already not Billed (someone rolled it back manually,
                // or it was never billed in the first place). Idempotent
                // no-op.
                skipped.Add(ToSummary(task));
                continue;
            }

            var restored = await workflow.RestoreFromBilledAsync(
                companyId,
                task.Id,
                actorUserId,
                string.IsNullOrWhiteSpace(reason)
                    ? "Credit note posted; task restored to completed."
                    : reason.Trim(),
                cancellationToken);
            processed.Add(ToSummary(restored));
        }

        // InvoiceId is Guid.Empty on this path -- the caller (credit
        // note hook) doesn't have one invoice; it has a set of tasks.
        return new TaskBillingResult
        {
            InvoiceId = Guid.Empty,
            ProcessedTasks = processed,
            SkippedTasks = skipped,
        };
    }

    public async Task<TaskBillingResult> MarkLinesAsBilledAsync(
        CompanyId companyId,
        string sourceType,
        Guid sourceId,
        Guid? customerId,
        IReadOnlyList<TaskLineBillingMapping> mappings,
        UserId actorUserId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required for line-level task billing.");
        }
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            throw new InvalidOperationException("Source type ('invoice' | 'sales_receipt') is required.");
        }
        if (sourceId == Guid.Empty)
        {
            throw new InvalidOperationException("Source document id is required.");
        }
        if (actorUserId.Value is null)
        {
            throw new InvalidOperationException("An acting user is required.");
        }
        if (mappings is null || mappings.Count == 0)
        {
            throw new InvalidOperationException("At least one task-line mapping is required.");
        }

        // Sanitize: drop any mappings missing required ids.
        var sanitized = mappings
            .Where(static m => m.TaskId != Guid.Empty && m.TaskLineId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (sanitized.Length == 0)
        {
            throw new InvalidOperationException("At least one non-empty (taskId, taskLineId) pair is required.");
        }

        // Group by task so the per-task validation and recompute each
        // see all of that task's mappings together. Distinct task ids
        // drive the cross-customer check.
        var byTask = sanitized
            .GroupBy(m => m.TaskId)
            .ToDictionary(g => g.Key, g => g.ToArray());

        // First pass: pre-flight validation. We refuse to start writing
        // if any task fails — matches the all-or-nothing semantics of
        // the legacy header-level MarkAsBilledAsync path.
        foreach (var taskId in byTask.Keys)
        {
            var task = await store.GetAsync(companyId, taskId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Task '{taskId:D}' was not found in the active company. Cross-company billing is not permitted.");

            if (customerId.HasValue && task.CustomerId != customerId.Value)
            {
                throw new InvalidOperationException(
                    $"Task '{task.TaskNo}' is attached to a different customer than the source document. A single source cannot bill task lines from mixed customers.");
            }

            // Status gate: only Open / Completed / PartiallyBilled may
            // accept new line-level bills. Billed (all-lines-covered)
            // and Canceled are terminal for new bills.
            if (task.Status is not (TaskStatus.Open or TaskStatus.Completed or TaskStatus.PartiallyBilled))
            {
                throw new InvalidOperationException(
                    $"Task '{task.TaskNo}' is in status '{task.Status.ToToken()}' and cannot accept new line-level bills.");
            }
        }

        // Second pass: write each line. The store's idempotent UPDATE
        // makes a same-source re-run a no-op; a different-source
        // re-stamp throws from the store (data-integrity signal that
        // bypasses the post-handler's soft-failure catch).
        var billedAtUtc = DateTimeOffset.UtcNow;
        var newlyStampedCount = 0;
        var skippedStampCount = 0;
        foreach (var mapping in sanitized)
        {
            var outcome = await store.MarkLineBilledAsync(
                companyId,
                mapping.TaskLineId,
                sourceType,
                sourceId,
                mapping.SourceLineId,
                billedAtUtc,
                cancellationToken);
            if (outcome.WasNewlyStamped) newlyStampedCount++;
            else skippedStampCount++;
        }

        // Third pass: per-task header status recompute. Each call is
        // idempotent — if the header is already at the right status
        // (e.g. a same-source re-run where nothing was newly stamped),
        // the workflow returns the current record without transition.
        var processed = new List<TaskSummary>(byTask.Count);
        var skipped = new List<TaskSummary>();
        foreach (var taskId in byTask.Keys)
        {
            var beforeStatus = (await store.GetAsync(companyId, taskId, cancellationToken))?.Status;
            var recomputed = await workflow.RecomputeAndTransitionFromLinesAsync(
                companyId,
                taskId,
                sourceType,
                sourceId,
                actorUserId,
                cancellationToken);

            if (recomputed.Status != beforeStatus)
            {
                processed.Add(ToSummary(recomputed));
            }
            else
            {
                skipped.Add(ToSummary(recomputed));
            }
        }

        return new TaskBillingResult
        {
            // For invoice-sourced calls, the legacy field name "InvoiceId"
            // still applies. For sales-receipt-sourced calls the caller
            // ignores this field (the source id lives in the wire-shape
            // outcome the caller wraps around our result).
            InvoiceId = sourceId,
            ProcessedTasks = processed,
            SkippedTasks = skipped,
        };
    }

    private static TaskBillingResult Empty(Guid invoiceId) => new()
    {
        InvoiceId = invoiceId,
        ProcessedTasks = Array.Empty<TaskSummary>(),
        SkippedTasks = Array.Empty<TaskSummary>(),
    };

    private static TaskSummary ToSummary(TaskRecord record) => new()
    {
        Id = record.Id,
        CompanyId = record.CompanyId,
        TaskNo = record.TaskNo,
        Title = record.Title,
        CustomerId = record.CustomerId,
        AssignedToUserId = record.AssignedToUserId,
        Status = record.Status,
        ServiceDate = record.ServiceDate,
        TotalBillableValue = record.TotalBillableValue,
        CurrencyCode = record.CurrencyCode,
        UpdatedAtUtc = record.UpdatedAtUtc,
    };
}
