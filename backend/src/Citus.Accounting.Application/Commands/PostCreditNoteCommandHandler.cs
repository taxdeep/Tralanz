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

    private async Task<CreditNoteTaskRollbackOutcome?> TryRollbackLinkedTasksAsync(
        PostCreditNoteCommand command,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> taskIds;
        try
        {
            taskIds = await _documents.ListLinkedTaskIdsAsync(
                command.CompanyId,
                command.DocumentId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return new CreditNoteTaskRollbackOutcome(0, 0, ex.Message);
        }

        if (taskIds.Count == 0)
        {
            return null;
        }

        try
        {
            var result = await _taskBilling.RollbackByTaskIdsAsync(
                command.CompanyId,
                taskIds,
                command.UserId,
                reason: null,
                cancellationToken);
            return new CreditNoteTaskRollbackOutcome(
                ProcessedCount: result.ProcessedTasks.Count,
                SkippedCount: result.SkippedTasks.Count,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new CreditNoteTaskRollbackOutcome(0, 0, ex.Message);
        }
    }
}
