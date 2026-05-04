using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed record PostInvoiceCommand(
    CompanyId CompanyId,
    Guid DocumentId,
    UserId UserId,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey);

public sealed record PostInvoiceCommandResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<InvoiceAutoCogsOutcome> AutoPostedCogs)
{
    public static PostInvoiceCommandResult FromPostingResult(PostingResult result) =>
        new(
            result.JournalEntryId,
            result.JournalEntryDisplayNumber,
            result.Status,
            result.PostedAt,
            result.Warnings,
            Array.Empty<InvoiceAutoCogsOutcome>());
}

/// <summary>
/// Per-sales-issue COGS-post outcome surfaced by
/// <see cref="PostInvoiceCommandHandler"/> after the invoice journal entry
/// commits. One entry per linked sales-issue. Soft-failure semantics: a
/// failed COGS post does not roll back the invoice — the workbench
/// (<c>/company/inventory/cogs-postings</c>) remains as the recovery path.
/// </summary>
public sealed record InvoiceAutoCogsOutcome(
    Guid SalesIssueDocumentId,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    bool AlreadyPosted,
    bool Succeeded,
    string? ErrorMessage);
