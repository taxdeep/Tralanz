using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed class PostReceivePaymentCommandHandler
{
    private readonly IReceivePaymentDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly ISettlementApplicationRepository _settlements;
    private readonly IUnitOfWork _unitOfWork;

    public PostReceivePaymentCommandHandler(
        IReceivePaymentDocumentRepository documents,
        IPostingEngine postingEngine,
        ISettlementApplicationRepository settlements,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _settlements = settlements ?? throw new ArgumentNullException(nameof(settlements));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostReceivePaymentCommandResult> HandleAsync(
        PostReceivePaymentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var existingPost = await _documents.GetPostedResultAsync(command.CompanyId, command.DocumentId, ct);
            if (existingPost is not null)
            {
                return new PostReceivePaymentCommandResult(
                    existingPost.JournalEntryId,
                    existingPost.JournalEntryDisplayNumber,
                    "posted",
                    existingPost.PostedAt,
                    Array.Empty<string>());
            }

            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException("Receive payment document was not found in the active company context.");
            }

            var acceptedFxSnapshotId =
                command.AcceptedFxSnapshotId ??
                (document.FxSnapshot is { SnapshotId: var snapshotId } && snapshotId != Guid.Empty
                    ? snapshotId
                    : null);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"receive-payment:{command.CompanyId}:{command.DocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                acceptedFxSnapshotId,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);
            await _settlements.ApplyReceivePaymentAsync(document, command.UserId, ct);
            await _documents.ParkExtraDepositAsync(document, command.UserId, ct);
            return PostReceivePaymentCommandResult.FromPostingResult(result);
        }, cancellationToken);
    }
}
