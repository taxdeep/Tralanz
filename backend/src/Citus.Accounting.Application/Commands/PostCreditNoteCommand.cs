using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed record PostCreditNoteCommand(
    CompanyId CompanyId,
    Guid DocumentId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record PostCreditNoteCommandResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt,
    IReadOnlyList<string> Warnings,
    CreditNoteTaskRollbackOutcome? TaskRollback = null)
{
    public static PostCreditNoteCommandResult FromPostingResult(PostingResult result) =>
        new(
            result.JournalEntryId,
            result.JournalEntryDisplayNumber,
            result.Status,
            result.PostedAt,
            result.Warnings,
            TaskRollback: null);
}

/// <summary>
/// Outcome of the Step 2 Task-billing rollback (<c>Billed -> Completed</c>)
/// triggered after a successful credit-note post when the credit note
/// has lines linked to one or more tasks. Null on the result when the
/// credit note carries no task_id-linked lines (the common case for a
/// standalone customer credit). Soft-failure: failure here does NOT
/// roll back the credit note — operator resolves via the Tasks page.
/// </summary>
public sealed record CreditNoteTaskRollbackOutcome(
    int ProcessedCount,
    int SkippedCount,
    string? ErrorMessage);
