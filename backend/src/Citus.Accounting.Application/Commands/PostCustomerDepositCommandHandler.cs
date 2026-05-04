using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// Two-phase: repo prepares + persists the customer_deposits +
/// ar_open_items rows and returns the posting document; engine then
/// journalises (Dr Bank / Cr Customer Deposit). Both phases run inside
/// a single unit of work so a posting failure rolls the persistence
/// back. Idempotency is journal-layer (source_type='customer_deposit',
/// source_id=deposit.id) — re-running on the same prepared deposit is
/// safe via the engine's existing source-id check.
/// </summary>
public sealed class PostCustomerDepositCommandHandler
{
    private readonly ICustomerDepositPostingRepository _postingRepository;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public PostCustomerDepositCommandHandler(
        ICustomerDepositPostingRepository postingRepository,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _postingRepository = postingRepository ?? throw new ArgumentNullException(nameof(postingRepository));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostCustomerDepositCommandResult> HandleAsync(
        PostCustomerDepositCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var preparation = await _postingRepository.PreparePostingDocumentAsync(
                command.CompanyId,
                command.UserId,
                new CustomerDepositPostingRequest(
                    SalesOrderId: command.SalesOrderId,
                    CustomerId: command.CustomerId,
                    DepositToAccountId: command.DepositToAccountId,
                    AmountTx: command.AmountTx,
                    DocumentDate: command.DocumentDate,
                    Memo: command.Memo,
                    IdempotencyKey: command.IdempotencyKey),
                ct);

            if (preparation.Document is null)
            {
                throw new InvalidOperationException(
                    "Customer deposit preparation returned no document. " +
                    "(Idempotency replay is not implemented in this iter.)");
            }

            var document = preparation.Document;

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"customer-deposit:{command.CompanyId.Value}:{document.Id}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            return new PostCustomerDepositCommandResult(
                CustomerDepositId: document.Id,
                DisplayNumber: document.DisplayNumber.Value,
                JournalEntryId: result.JournalEntryId,
                JournalEntryDisplayNumber: result.JournalEntryDisplayNumber,
                AmountBase: document.AmountBase);
        }, cancellationToken);
    }
}
