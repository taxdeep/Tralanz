using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Application.Commands;

public sealed class PrepareFxRevaluationCascadeUnwindBatchCommandHandler
{
    private readonly IFxRevaluationDocumentRepository _documents;
    private readonly IUnitOfWork _unitOfWork;

    public PrepareFxRevaluationCascadeUnwindBatchCommandHandler(
        IFxRevaluationDocumentRepository documents,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PrepareFxRevaluationCascadeUnwindBatchCommandResult> HandleAsync(
        PrepareFxRevaluationCascadeUnwindBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var plan = await _documents.GetCascadeUnwindPlanAsync(
                command.CompanyId,
                command.RequestedDocumentId,
                ct);

            var preparedDraft = await _documents.PrepareNextPeriodUnwindDraftAsync(
                new FxRevaluationUnwindPreparation(
                    command.CompanyId,
                    command.UserId,
                    plan.NextDocumentId,
                    command.UnwindDate,
                    command.Memo),
                ct);

            return new PrepareFxRevaluationCascadeUnwindBatchCommandResult(
                plan.RequestedDocumentId,
                plan.RequestedDisplayNumber,
                plan.NextDocumentId,
                plan.NextDisplayNumber,
                plan.RequestedBatchIsTail,
                plan.ActiveRevaluationChain.Count,
                preparedDraft.DocumentId,
                preparedDraft.EntityNumber,
                preparedDraft.DisplayNumber,
                preparedDraft.PreparedLineCount,
                preparedDraft.Status);
        }, cancellationToken);
    }
}
