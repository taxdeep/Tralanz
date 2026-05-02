using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// Cash-in-hand sale post command. Same shape as
/// <see cref="PostInvoiceCommand"/> minus the AR control account
/// concerns — sales receipts don't open an open item.
/// </summary>
public sealed record PostSalesReceiptCommand(
    CompanyId CompanyId,
    Guid DocumentId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record PostSalesReceiptCommandResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt,
    IReadOnlyList<string> Warnings)
{
    public static PostSalesReceiptCommandResult FromPostingResult(PostingResult result) =>
        new(
            result.JournalEntryId,
            result.JournalEntryDisplayNumber,
            result.Status,
            result.PostedAt,
            result.Warnings);
}
