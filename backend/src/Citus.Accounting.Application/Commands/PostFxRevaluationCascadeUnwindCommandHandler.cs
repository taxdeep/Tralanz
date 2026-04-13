using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed class PostFxRevaluationCascadeUnwindCommandHandler
{
    private readonly IFxRevaluationDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly IFxRevaluationApplyRepository _revaluationApplier;
    private readonly IUnitOfWork _unitOfWork;

    public PostFxRevaluationCascadeUnwindCommandHandler(
        IFxRevaluationDocumentRepository documents,
        IPostingEngine postingEngine,
        IFxRevaluationApplyRepository revaluationApplier,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _revaluationApplier = revaluationApplier ?? throw new ArgumentNullException(nameof(revaluationApplier));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostFxRevaluationCascadeUnwindCommandResult> HandleAsync(
        PostFxRevaluationCascadeUnwindCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var initialPlan = await _documents.GetCascadeUnwindPlanAsync(
                command.CompanyId,
                command.RequestedDocumentId,
                ct);
            var requestedBatchWasTail = initialPlan.RequestedBatchIsTail;
            var postedSteps = new List<PostFxRevaluationCascadeUnwindStepResult>();

            while (true)
            {
                var plan = postedSteps.Count == 0
                    ? initialPlan
                    : await _documents.GetCascadeUnwindPlanAsync(command.CompanyId, command.RequestedDocumentId, ct);

                var preparedDraft = await _documents.PrepareNextPeriodUnwindDraftAsync(
                    new FxRevaluationUnwindPreparation(
                        command.CompanyId,
                        command.UserId,
                        plan.NextDocumentId,
                        command.UnwindDate,
                        command.Memo),
                    ct);

                var draftDocument = await _documents.GetForPostingAsync(command.CompanyId, preparedDraft.DocumentId, ct);
                if (draftDocument is null)
                {
                    throw new InvalidOperationException("Prepared FX revaluation unwind draft could not be reloaded for posting.");
                }

                var acceptedFxSnapshotId =
                    draftDocument.FxSnapshot.SnapshotId == Guid.Empty
                        ? (Guid?)null
                        : draftDocument.FxSnapshot.SnapshotId;

                var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                    ? $"fx-revaluation-cascade:{command.CompanyId}:{command.RequestedDocumentId}:{plan.NextDocumentId}"
                    : $"{command.IdempotencyKey.Trim()}:{plan.NextDocumentId}";

                var postingResult = await _postingEngine.PostAsync(
                    draftDocument,
                    new PostingContext(
                        command.CompanyId,
                        command.UserId,
                        draftDocument.BaseCurrencyCode,
                        acceptedFxSnapshotId,
                        idempotencyKey,
                        DateTimeOffset.UtcNow),
                    ct);

                await _revaluationApplier.ApplyAsync(draftDocument, command.UserId, ct);

                postedSteps.Add(new PostFxRevaluationCascadeUnwindStepResult(
                    plan.NextDocumentId,
                    plan.NextDisplayNumber,
                    preparedDraft.DocumentId,
                    preparedDraft.DisplayNumber,
                    postingResult.JournalEntryId,
                    postingResult.JournalEntryDisplayNumber,
                    postingResult.PostedAt,
                    postingResult.Warnings));

                if (plan.NextDocumentId == command.RequestedDocumentId)
                {
                    return new PostFxRevaluationCascadeUnwindCommandResult(
                        initialPlan.RequestedDocumentId,
                        initialPlan.RequestedDisplayNumber,
                        requestedBatchWasTail,
                        postedSteps.Count,
                        postedSteps);
                }
            }
        }, cancellationToken);
    }
}
