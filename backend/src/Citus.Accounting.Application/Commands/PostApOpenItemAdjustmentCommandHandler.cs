using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed class PostApOpenItemAdjustmentCommandHandler
{
    private readonly IApOpenItemRepository _openItems;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public PostApOpenItemAdjustmentCommandHandler(
        IApOpenItemRepository openItems,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _openItems = openItems ?? throw new ArgumentNullException(nameof(openItems));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostApOpenItemAdjustmentCommandResult> HandleAsync(
        PostApOpenItemAdjustmentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var preparation = await _openItems.PrepareAdjustmentExecutionAsync(
                command.CompanyId,
                command.OpenItemId,
                command.RequestId,
                command.AdjustmentAccountId,
                command.AsOfDate,
                ct);

            if (preparation is null)
            {
                throw new InvalidOperationException("AP open item adjustment request was not found in the active company context.");
            }

            if (!preparation.Readiness.IsAvailable || !preparation.Readiness.PostingExecutionReady)
            {
                throw new InvalidOperationException(preparation.Readiness.Reason);
            }

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"ap-open-item-adjustment:{command.CompanyId}:{command.RequestId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                preparation.Document.BaseCurrencyCode,
                null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var postingResult = await _postingEngine.PostAsync(preparation.Document, postingContext, ct);
            var execution = await _openItems.CompleteAdjustmentExecutionAsync(
                command.CompanyId,
                command.OpenItemId,
                command.RequestId,
                command.UserId.Value,
                postingResult.JournalEntryId,
                postingResult.JournalEntryDisplayNumber,
                postingResult.PostedAt,
                ct);

            if (execution is null || !execution.Executed)
            {
                throw new InvalidOperationException("AP open item adjustment posting succeeded, but open-item completion could not be recorded.");
            }

            return new PostApOpenItemAdjustmentCommandResult(
                postingResult.JournalEntryId,
                postingResult.JournalEntryDisplayNumber,
                postingResult.Status,
                postingResult.PostedAt,
                execution.AdjustmentAmountTx,
                execution.AdjustmentAmountBase);
        }, cancellationToken);
    }
}
