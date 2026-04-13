using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed record PostVendorCreditApplicationCommand(
    CompanyId CompanyId,
    Guid DocumentId,
    UserId UserId,
    string? IdempotencyKey);

public sealed record PostVendorCreditApplicationCommandResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt)
{
    public static PostVendorCreditApplicationCommandResult FromPostingResult(PostingResult result) =>
        new(
            result.JournalEntryId,
            result.JournalEntryDisplayNumber,
            result.Status,
            result.PostedAt);
}
