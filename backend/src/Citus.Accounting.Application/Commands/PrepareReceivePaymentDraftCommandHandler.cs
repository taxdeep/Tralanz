using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Application.Commands;

public sealed class PrepareReceivePaymentDraftCommandHandler
{
    private readonly IReceivePaymentDocumentRepository _documents;
    private readonly IUnitOfWork _unitOfWork;

    public PrepareReceivePaymentDraftCommandHandler(
        IReceivePaymentDocumentRepository documents,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PrepareReceivePaymentDraftCommandResult> HandleAsync(
        PrepareReceivePaymentDraftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var result = await _documents.PrepareDraftAsync(
                new ReceivePaymentDraftPreparation(
                    command.CompanyId,
                    command.UserId,
                    command.CustomerId,
                    command.BankAccountId,
                    command.PaymentDate,
                    command.AcceptedFxSnapshotId,
                    command.Memo,
                    command.Lines),
                ct);

            return PrepareReceivePaymentDraftCommandResult.FromRepositoryResult(result);
        }, cancellationToken);
    }
}
