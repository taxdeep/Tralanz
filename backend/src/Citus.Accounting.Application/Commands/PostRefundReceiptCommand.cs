using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed record PostRefundReceiptCommand(
    CompanyId CompanyId,
    Guid DocumentId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record PostRefundReceiptCommandResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt,
    IReadOnlyList<string> Warnings,
    // H6-3: per-refund summary of the Task rollback hook. Null when
    // the refund has no task-linked lines (typical cash-refund case).
    // Reuses CreditNoteTaskRollbackOutcome (identical shape — keeps
    // us from minting a fresh record type just to rename three ints).
    CreditNoteTaskRollbackOutcome? TaskRollback = null)
{
    public static PostRefundReceiptCommandResult FromPostingResult(PostingResult result) =>
        new(
            result.JournalEntryId,
            result.JournalEntryDisplayNumber,
            result.Status,
            result.PostedAt,
            result.Warnings);
}
