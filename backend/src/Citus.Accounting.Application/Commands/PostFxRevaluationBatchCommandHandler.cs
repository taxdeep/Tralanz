using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed class PostFxRevaluationBatchCommandHandler
{
    private readonly IFxRevaluationDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly IFxRevaluationApplyRepository _revaluationApplier;
    private readonly IUnitOfWork _unitOfWork;

    public PostFxRevaluationBatchCommandHandler(
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

    public Task<PostFxRevaluationBatchCommandResult> HandleAsync(
        PostFxRevaluationBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException("FX revaluation batch was not found in the active company context.");
            }

            var acceptedFxSnapshotId =
                command.AcceptedFxSnapshotId ??
                (document.FxSnapshot is { SnapshotId: var snapshotId } && snapshotId != Guid.Empty
                    ? snapshotId
                    : null);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"fx-revaluation:{command.CompanyId}:{command.DocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                acceptedFxSnapshotId,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);
            await _revaluationApplier.ApplyAsync(document, command.UserId, ct);
            return PostFxRevaluationBatchCommandResult.FromPostingResult(result);
        }, cancellationToken);
    }
}
