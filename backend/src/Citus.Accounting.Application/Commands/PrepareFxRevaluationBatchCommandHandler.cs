using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Application.Commands;

public sealed class PrepareFxRevaluationBatchCommandHandler
{
    private readonly IFxRevaluationDocumentRepository _documents;
    private readonly IUnitOfWork _unitOfWork;

    public PrepareFxRevaluationBatchCommandHandler(
        IFxRevaluationDocumentRepository documents,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PrepareFxRevaluationBatchCommandResult> HandleAsync(
        PrepareFxRevaluationBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var result = await _documents.PrepareDraftAsync(
                new FxRevaluationDraftPreparation(
                    command.CompanyId,
                    command.UserId,
                    command.RevaluationDate,
                    command.TransactionCurrencyCode,
                    command.AcceptedFxSnapshotId,
                    command.IncludeAccountsReceivable,
                    command.IncludeAccountsPayable,
                    command.Memo),
                ct);

            return PrepareFxRevaluationBatchCommandResult.FromRepositoryResult(result);
        }, cancellationToken);
    }
}
