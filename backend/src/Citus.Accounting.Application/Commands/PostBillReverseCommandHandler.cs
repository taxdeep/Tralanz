using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// Orchestrates the bill-reverse compensation JE. Mirrors
/// <see cref="PostInvoiceReverseCommandHandler"/>: wraps in
/// IUnitOfWork.ExecuteAsync, dispatches the pre-flipped document through the
/// posting engine, and returns the previously-posted reverse JE on idempotent
/// re-runs.
/// </summary>
public sealed class PostBillReverseCommandHandler
{
    private readonly IBillReversePostingRepository _postingRepository;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public PostBillReverseCommandHandler(
        IBillReversePostingRepository postingRepository,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _postingRepository = postingRepository ?? throw new ArgumentNullException(nameof(postingRepository));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostBillReverseCommandResult> HandleAsync(
        PostBillReverseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var preparation = await _postingRepository.PreparePostingDocumentAsync(
                command.CompanyId,
                command.UserId,
                command.BillId,
                ct);

            if (preparation.Document is null)
            {
                if (preparation.ExistingJournalEntryId is null ||
                    preparation.ExistingJournalEntryId.Value == Guid.Empty)
                {
                    throw new InvalidOperationException(
                        "Bill reverse posting repository returned no document and no existing reverse journal entry.");
                }
                return new PostBillReverseCommandResult(
                    BillId: command.BillId,
                    JournalEntryId: preparation.ExistingJournalEntryId.Value,
                    JournalEntryDisplayNumber: preparation.ExistingJournalEntryDisplayNumber ?? string.Empty,
                    AlreadyReversed: true);
            }

            var document = preparation.Document;

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"bill-reverse:{command.CompanyId.Value}:{command.BillId:D}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            return new PostBillReverseCommandResult(
                BillId: command.BillId,
                JournalEntryId: result.JournalEntryId,
                JournalEntryDisplayNumber: result.JournalEntryDisplayNumber,
                AlreadyReversed: false);
        }, cancellationToken);
    }
}
