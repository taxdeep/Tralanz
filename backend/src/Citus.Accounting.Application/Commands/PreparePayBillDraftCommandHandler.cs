using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Application.Commands;

public sealed class PreparePayBillDraftCommandHandler
{
    private readonly IPayBillDocumentRepository _documents;
    private readonly IUnitOfWork _unitOfWork;

    public PreparePayBillDraftCommandHandler(
        IPayBillDocumentRepository documents,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PreparePayBillDraftCommandResult> HandleAsync(
        PreparePayBillDraftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var result = await _documents.PrepareDraftAsync(
                new PayBillDraftPreparation(
                    command.CompanyId,
                    command.UserId,
                    command.VendorId,
                    command.BankAccountId,
                    command.PaymentDate,
                    command.AcceptedFxSnapshotId,
                    command.Memo,
                    command.Lines),
                ct);

            return PreparePayBillDraftCommandResult.FromRepositoryResult(result);
        }, cancellationToken);
    }
}
