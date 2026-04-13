using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed record PostManualJournalCommand(
    CompanyId CompanyId,
    Guid DocumentId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record PostManualJournalCommandResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt,
    IReadOnlyList<string> Warnings)
{
    public static PostManualJournalCommandResult FromPostingResult(PostingResult result) =>
        new(
            result.JournalEntryId,
            result.JournalEntryDisplayNumber,
            result.Status,
            result.PostedAt,
            result.Warnings);
}
