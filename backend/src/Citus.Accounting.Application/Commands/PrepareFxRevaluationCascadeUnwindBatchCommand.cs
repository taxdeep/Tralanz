using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

public sealed record PrepareFxRevaluationCascadeUnwindBatchCommand(
    CompanyId CompanyId,
    Guid RequestedDocumentId,
    UserId UserId,
    DateOnly UnwindDate,
    string? Memo);

public sealed record PrepareFxRevaluationCascadeUnwindBatchCommandResult(
    Guid RequestedDocumentId,
    string RequestedDisplayNumber,
    Guid TargetDocumentId,
    string TargetDisplayNumber,
    bool RequestedBatchIsTail,
    int ActiveRevaluationCount,
    Guid DraftDocumentId,
    string DraftEntityNumber,
    string DraftDisplayNumber,
    int PreparedLineCount,
    string Status);
