using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

public sealed record PostFxRevaluationCascadeUnwindCommand(
    CompanyId CompanyId,
    Guid RequestedDocumentId,
    UserId UserId,
    DateOnly UnwindDate,
    string? Memo,
    string? IdempotencyKey);

public sealed record PostFxRevaluationCascadeUnwindStepResult(
    Guid SourceDocumentId,
    string SourceDisplayNumber,
    Guid UnwindDocumentId,
    string UnwindDisplayNumber,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    DateTimeOffset PostedAt,
    IReadOnlyList<string> Warnings);

public sealed record PostFxRevaluationCascadeUnwindCommandResult(
    Guid RequestedDocumentId,
    string RequestedDisplayNumber,
    bool RequestedBatchWasTail,
    int PostedStepCount,
    IReadOnlyList<PostFxRevaluationCascadeUnwindStepResult> PostedSteps);
