using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// M5 iter 4: applies open SO-linked customer deposits against the
/// just-posted invoice. Pro-rata (invoice's share of the SO total) and
/// FIFO across deposits. Soft-failure caller pattern: invoke this from
/// PostInvoiceCommandHandler AFTER the invoice JE commits — if no
/// applicable deposits exist or the application errors out, the invoice
/// stays cleanly posted.
/// </summary>
public sealed record ApplyCustomerDepositsToInvoiceCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid InvoiceDocumentId);

public sealed record ApplyCustomerDepositsToInvoiceCommandResult(
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    decimal TotalAppliedBase,
    IReadOnlyList<CustomerDepositApplicationOutcome> Applications);

public sealed class ApplyCustomerDepositsToInvoiceCommandHandler
{
    private readonly ICustomerDepositApplicationRepository _repository;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public ApplyCustomerDepositsToInvoiceCommandHandler(
        ICustomerDepositApplicationRepository repository,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<ApplyCustomerDepositsToInvoiceCommandResult> HandleAsync(
        ApplyCustomerDepositsToInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var preparation = await _repository.PrepareApplicationAsync(
                command.CompanyId,
                command.UserId,
                command.InvoiceDocumentId,
                ct);

            // Nothing applied — return early with a no-op result.
            if (preparation.Document is null)
            {
                return new ApplyCustomerDepositsToInvoiceCommandResult(
                    JournalEntryId: null,
                    JournalEntryDisplayNumber: null,
                    TotalAppliedBase: 0m,
                    Applications: preparation.Applications);
            }

            var document = preparation.Document;
            var idempotencyKey = $"customer-deposit-application:{command.CompanyId.Value}:{document.Id}";

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            return new ApplyCustomerDepositsToInvoiceCommandResult(
                JournalEntryId: result.JournalEntryId,
                JournalEntryDisplayNumber: result.JournalEntryDisplayNumber,
                TotalAppliedBase: document.TotalAmountBase,
                Applications: preparation.Applications);
        }, cancellationToken);
    }
}
