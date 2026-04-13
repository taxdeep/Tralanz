using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

public sealed record PrepareFxRevaluationUnwindBatchCommand(
    CompanyId CompanyId,
    Guid ReversalOfDocumentId,
    UserId UserId,
    DateOnly UnwindDate,
    string? Memo);

public sealed record PrepareFxRevaluationUnwindBatchCommandResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    int PreparedLineCount,
    string Status)
{
    public static PrepareFxRevaluationUnwindBatchCommandResult FromRepositoryResult(
        FxRevaluationDraftPreparationResult result) =>
        new(
            result.DocumentId,
            result.EntityNumber,
            result.DisplayNumber,
            result.PreparedLineCount,
            result.Status);
}
