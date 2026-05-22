using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;
using Citus.Modules.Tasks.Application.Contracts;

namespace Citus.Accounting.Application.Commands;

public sealed class PostCreditNoteCommandHandler
{
    private readonly ICreditNoteDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly IArOpenItemRepository _openItems;
    private readonly ITaskBillingCoordinator _taskBilling;
    private readonly IUnitOfWork _unitOfWork;

    public PostCreditNoteCommandHandler(
        ICreditNoteDocumentRepository documents,
        IPostingEngine postingEngine,
        IArOpenItemRepository openItems,
        ITaskBillingCoordinator taskBilling,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _openItems = openItems ?? throw new ArgumentNullException(nameof(openItems));
        _taskBilling = taskBilling ?? throw new ArgumentNullException(nameof(taskBilling));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<PostCreditNoteCommandResult> HandleAsync(
        PostCreditNoteCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1 — transactional credit-note post (JE + AR open item).
        var postResult = await _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException("Credit note document was not found in the active company context.");
            }

            var acceptedFxSnapshotId =
                command.AcceptedFxSnapshotId ??
                (document.FxSnapshot is { SnapshotId: var snapshotId } && snapshotId != Guid.Empty
                    ? snapshotId
                    : null);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"credit-note:{command.CompanyId}:{command.DocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                acceptedFxSnapshotId,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            var originalAmountBase = Math.Round(
                document.TotalAmount * (document.FxSnapshot?.Rate ?? 1m),
                2,
                MidpointRounding.ToEven);

            await _openItems.EnsureForCreditNoteAsync(document, originalAmountBase, ct);
            return PostCreditNoteCommandResult.FromPostingResult(result);
        }, cancellationToken);

        // Step 2 — Task billing rollback (mirror of PostInvoice Step 5).
        // If any credit-note line has task_id, flip the linked tasks
        // Billed -> Completed via the coordinator. Soft-failure: the
        // credit note already posted; a rollback failure (e.g. a task
        // already manually restored, data drift) is reported in the
        // result and the operator resolves from the Tasks page.
        var taskRollback = await TryRollbackLinkedTasksAsync(command, cancellationToken);

        return postResult with { TaskRollback = taskRollback };
    }

    /// <summary>
    /// H6-3: line-level when credit-note lines carry task_line_id;
    /// legacy whole-task otherwise. Both can co-exist on one credit
    /// note. Soft-failure preserved — any error surfaces in the
    /// outcome but the credit JE stays committed.
    /// </summary>
    private async Task<CreditNoteTaskRollbackOutcome?> TryRollbackLinkedTasksAsync(
        PostCreditNoteCommand command,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CreditNoteLineTaskLink> linkRows;
        try
        {
            linkRows = await _documents.ListLinkedTaskLineMappingsAsync(
                command.CompanyId,
                command.DocumentId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return new CreditNoteTaskRollbackOutcome(0, 0, ex.Message);
        }

        if (linkRows.Count == 0)
        {
            return null;
        }

        var lineLevelIds = linkRows
            .Where(r => r.TaskLineId.HasValue)
            .Select(r => r.TaskLineId!.Value)
            .Distinct()
            .ToArray();
        var tasksHandledByLinePath = linkRows
            .Where(r => r.TaskLineId.HasValue)
            .Select(r => r.TaskId)
            .ToHashSet();
        var legacyTaskIds = linkRows
            .Where(r => !r.TaskLineId.HasValue && !tasksHandledByLinePath.Contains(r.TaskId))
            .Select(r => r.TaskId)
            .Distinct()
            .ToArray();

        var processedCount = 0;
        var skippedCount = 0;

        if (lineLevelIds.Length > 0)
        {
            try
            {
                // The originating source identity isn't on the credit
                // note line — credit notes can reverse multiple
                // sources. The rollback clears the line stamps
                // regardless of source; the recompute step then
                // restores the affected task headers.
                var result = await _taskBilling.RollbackLinesAsync(
                    command.CompanyId,
                    sourceType: "credit_note",
                    sourceId: command.DocumentId,
                    lineLevelIds,
                    command.UserId,
                    reason: $"Credit note {command.DocumentId:D} reversed billed lines.",
                    cancellationToken);
                processedCount += result.ProcessedTasks.Count;
                skippedCount += result.SkippedTasks.Count;
            }
            catch (Exception ex)
            {
                return new CreditNoteTaskRollbackOutcome(processedCount, skippedCount, ex.Message);
            }
        }

        if (legacyTaskIds.Length > 0)
        {
            try
            {
                var result = await _taskBilling.RollbackByTaskIdsAsync(
                    command.CompanyId,
                    legacyTaskIds,
                    command.UserId,
                    reason: null,
                    cancellationToken);
                processedCount += result.ProcessedTasks.Count;
                skippedCount += result.SkippedTasks.Count;
            }
            catch (Exception ex)
            {
                return new CreditNoteTaskRollbackOutcome(processedCount, skippedCount, ex.Message);
            }
        }

        return new CreditNoteTaskRollbackOutcome(processedCount, skippedCount, ErrorMessage: null);
    }
}
