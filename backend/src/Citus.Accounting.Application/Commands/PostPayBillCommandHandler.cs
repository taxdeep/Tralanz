using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed class PostPayBillCommandHandler
{
    private readonly IPayBillDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly ISettlementApplicationRepository _settlements;
    private readonly IUnitOfWork _unitOfWork;

    public PostPayBillCommandHandler(
        IPayBillDocumentRepository documents,
        IPostingEngine postingEngine,
        ISettlementApplicationRepository settlements,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _settlements = settlements ?? throw new ArgumentNullException(nameof(settlements));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostPayBillCommandResult> HandleAsync(
        PostPayBillCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var existingPost = await _documents.GetPostedResultAsync(command.CompanyId, command.DocumentId, ct);
            if (existingPost is not null)
            {
                return new PostPayBillCommandResult(
                    existingPost.JournalEntryId,
                    existingPost.JournalEntryDisplayNumber,
                    "posted",
                    existingPost.PostedAt,
                    Array.Empty<string>());
            }

            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException("Pay bill document was not found in the active company context.");
            }

            var acceptedFxSnapshotId =
                command.AcceptedFxSnapshotId ??
                (document.FxSnapshot is { SnapshotId: var snapshotId } && snapshotId != Guid.Empty
                    ? snapshotId
                    : null);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"pay-bill:{command.CompanyId}:{command.DocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                acceptedFxSnapshotId,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);
            await _settlements.ApplyPayBillAsync(document, command.UserId, ct);
            return PostPayBillCommandResult.FromPostingResult(result);
        }, cancellationToken);
    }
}
