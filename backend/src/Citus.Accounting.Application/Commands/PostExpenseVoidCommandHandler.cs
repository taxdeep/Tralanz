using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// H1: orchestrates the Expense Void compensation JE. Mirrors the
/// PostSalesIssueCogsCommandHandler shape — wraps in
/// IUnitOfWork.ExecuteAsync, dispatches through the posting engine,
/// returns the previously-posted reverse JE on idempotent re-runs.
/// </summary>
public sealed class PostExpenseVoidCommandHandler
{
    private readonly IExpenseVoidPostingRepository _postingRepository;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public PostExpenseVoidCommandHandler(
        IExpenseVoidPostingRepository postingRepository,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _postingRepository = postingRepository ?? throw new ArgumentNullException(nameof(postingRepository));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostExpenseVoidCommandResult> HandleAsync(
        PostExpenseVoidCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var preparation = await _postingRepository.PreparePostingDocumentAsync(
                command.CompanyId,
                command.UserId,
                command.ExpenseId,
                ct);

            if (preparation.Document is null)
            {
                if (preparation.ExistingJournalEntryId is null ||
                    preparation.ExistingJournalEntryId.Value == Guid.Empty)
                {
                    throw new InvalidOperationException(
                        "Expense void posting repository returned no document and no existing reverse journal entry.");
                }
                return new PostExpenseVoidCommandResult(
                    ExpenseId: command.ExpenseId,
                    JournalEntryId: preparation.ExistingJournalEntryId.Value,
                    JournalEntryDisplayNumber: preparation.ExistingJournalEntryDisplayNumber ?? string.Empty,
                    AlreadyVoided: true);
            }

            var document = preparation.Document;

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"expense-void:{command.CompanyId.Value}:{command.ExpenseId:D}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            return new PostExpenseVoidCommandResult(
                ExpenseId: command.ExpenseId,
                JournalEntryId: result.JournalEntryId,
                JournalEntryDisplayNumber: result.JournalEntryDisplayNumber,
                AlreadyVoided: false);
        }, cancellationToken);
    }
}
