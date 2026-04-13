using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Application.Commands;

public sealed class PrepareFxRevaluationUnwindBatchCommandHandler
{
    private readonly IFxRevaluationDocumentRepository _documents;
    private readonly IUnitOfWork _unitOfWork;

    public PrepareFxRevaluationUnwindBatchCommandHandler(
        IFxRevaluationDocumentRepository documents,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PrepareFxRevaluationUnwindBatchCommandResult> HandleAsync(
        PrepareFxRevaluationUnwindBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var result = await _documents.PrepareNextPeriodUnwindDraftAsync(
                new FxRevaluationUnwindPreparation(
                    command.CompanyId,
                    command.UserId,
                    command.ReversalOfDocumentId,
                    command.UnwindDate,
                    command.Memo),
                ct);

            return PrepareFxRevaluationUnwindBatchCommandResult.FromRepositoryResult(result);
        }, cancellationToken);
    }
}
