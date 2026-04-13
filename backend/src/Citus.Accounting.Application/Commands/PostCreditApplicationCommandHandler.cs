using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed class PostCreditApplicationCommandHandler
{
    private readonly ICreditApplicationDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly ISettlementApplicationRepository _settlements;
    private readonly IUnitOfWork _unitOfWork;

    public PostCreditApplicationCommandHandler(
        ICreditApplicationDocumentRepository documents,
        IPostingEngine postingEngine,
        ISettlementApplicationRepository settlements,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _settlements = settlements ?? throw new ArgumentNullException(nameof(settlements));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostCreditApplicationCommandResult> HandleAsync(
        PostCreditApplicationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException("Credit application document was not found in the active company context.");
            }

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"credit-application:{command.CompanyId}:{command.DocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);
            await _settlements.ApplyCreditApplicationAsync(document, command.UserId, ct);
            return PostCreditApplicationCommandResult.FromPostingResult(result);
        }, cancellationToken);
    }
}
