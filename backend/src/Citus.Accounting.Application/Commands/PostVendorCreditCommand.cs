using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed record PostVendorCreditCommand(
    CompanyId CompanyId,
    Guid DocumentId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record PostVendorCreditCommandResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt,
    IReadOnlyList<string> Warnings)
{
    public static PostVendorCreditCommandResult FromPostingResult(PostingResult result) =>
        new(
            result.JournalEntryId,
            result.JournalEntryDisplayNumber,
            result.Status,
            result.PostedAt,
            result.Warnings);
}
