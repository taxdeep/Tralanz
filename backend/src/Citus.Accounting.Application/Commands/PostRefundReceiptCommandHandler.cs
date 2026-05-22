using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;
using Citus.Modules.Tasks.Application.Contracts;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// Mirror of <see cref="PostSalesReceiptCommandHandler"/>. Refund
/// Receipt is the reverse of Sales Receipt; H6-3 adds the
/// post-handler hook to release task_lines that the original sales
/// receipt billed. Soft-failure: any error during rollback is
/// recorded in the result but the refund JE stays committed.
/// </summary>
public sealed class PostRefundReceiptCommandHandler
{
    private readonly IRefundReceiptDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly ITaskBillingCoordinator _taskBilling;
    private readonly IUnitOfWork _unitOfWork;

    public PostRefundReceiptCommandHandler(
        IRefundReceiptDocumentRepository documents,
        IPostingEngine postingEngine,
        ITaskBillingCoordinator taskBilling,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _taskBilling = taskBilling ?? throw new ArgumentNullException(nameof(taskBilling));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<PostRefundReceiptCommandResult> HandleAsync(
        PostRefundReceiptCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1 — refund JE post inside the UoW.
        var postResult = await _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException(
                    "Refund receipt document was not found in the active company context.");
            }

            var acceptedFxSnapshotId =
                command.AcceptedFxSnapshotId ??
                (document.FxSnapshot is { SnapshotId: var snapshotId } && snapshotId != Guid.Empty
                    ? snapshotId
                    : null);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"refund_receipt:{command.CompanyId}:{command.DocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                acceptedFxSnapshotId,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);
            return PostRefundReceiptCommandResult.FromPostingResult(result);
        }, cancellationToken);

        // Step 2 — H6-3: release linked task_lines. Soft-failure
        // mirrors the credit-note flow.
        var taskRollback = await TryRollbackLinkedTasksAsync(command, cancellationToken);

        return postResult with { TaskRollback = taskRollback };
    }

    /// <summary>
    /// Refund-receipt mirror of the credit-note rollback hook. Lines
    /// with task_line_id route through the H6-2 line-level path;
    /// task_id-only rows fall back to the legacy whole-task rollback.
    /// </summary>
    private async Task<CreditNoteTaskRollbackOutcome?> TryRollbackLinkedTasksAsync(
        PostRefundReceiptCommand command,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RefundReceiptLineTaskLink> linkRows;
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
                var result = await _taskBilling.RollbackLinesAsync(
                    command.CompanyId,
                    sourceType: "refund_receipt",
                    sourceId: command.DocumentId,
                    lineLevelIds,
                    command.UserId,
                    reason: $"Refund receipt {command.DocumentId:D} reversed billed lines.",
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
