using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed class PostReceiptGrIrSettlementJournalCommandHandler
{
    private readonly IReceiptGrIrSettlementPostingRepository _postingRepository;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public PostReceiptGrIrSettlementJournalCommandHandler(
        IReceiptGrIrSettlementPostingRepository postingRepository,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _postingRepository = postingRepository ?? throw new ArgumentNullException(nameof(postingRepository));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostReceiptGrIrSettlementJournalCommandResult> HandleAsync(
        PostReceiptGrIrSettlementJournalCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _postingRepository.PreparePostingDocumentAsync(
                command.CompanyId,
                command.UserId,
                command.ReceiptDocumentId,
                command.SettlementBatchId,
                ct);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"receipt-grir-ap-settlement-journal:{command.CompanyId}:{command.SettlementBatchId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);
            await _postingRepository.CompletePostingAsync(
                command.CompanyId,
                command.UserId,
                command.SettlementBatchId,
                result.JournalEntryId,
                result.JournalEntryDisplayNumber,
                ct);

            return new PostReceiptGrIrSettlementJournalCommandResult(
                command.ReceiptDocumentId,
                command.SettlementBatchId,
                result.JournalEntryId,
                result.JournalEntryDisplayNumber,
                result.Status,
                result.PostedAt);
        }, cancellationToken);
    }
}

public sealed record PostReceiptGrIrSettlementJournalCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid ReceiptDocumentId,
    Guid SettlementBatchId,
    string? IdempotencyKey);

public sealed record PostReceiptGrIrSettlementJournalCommandResult(
    Guid ReceiptDocumentId,
    Guid SettlementBatchId,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt);
