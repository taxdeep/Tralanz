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
    IReadOnlyList<InvoiceAutoCogsOutcome> AutoPostedCogs,
    InvoiceDepositApplicationOutcome? AppliedCustomerDeposits,
    InvoiceDropShipCogsOutcome? DropShipCogs)
{
    public static PostInvoiceCommandResult FromPostingResult(PostingResult result) =>
        new(
            result.JournalEntryId,
            result.JournalEntryDisplayNumber,
            result.Status,
            result.PostedAt,
            result.Warnings,
            Array.Empty<InvoiceAutoCogsOutcome>(),
            AppliedCustomerDeposits: null,
            DropShipCogs: null);
}

/// <summary>
/// Per-invoice summary of M5 iter 4 deposit clearing. Null on the
/// invoice result when no deposit application happened (no SO link, no
/// open deposits, share=0, or the apply failed soft). When non-null,
/// at least one deposit slice was applied and a Dr Customer Deposit /
/// Cr AR JE was posted.
/// </summary>
public sealed record InvoiceDepositApplicationOutcome(
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    decimal TotalAppliedBase,
    IReadOnlyList<InvoiceDepositApplicationSlice> Slices,
    string? ErrorMessage);

public sealed record InvoiceDepositApplicationSlice(
    Guid CustomerDepositId,
    string CustomerDepositDisplayNumber,
    decimal AppliedAmountBase,
    bool DepositFullyClosed);

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

/// <summary>
/// M6 iter 3 outcome surfaced by <see cref="PostInvoiceCommandHandler"/>
/// when an invoice carries one or more drop-ship lines. NoOp = true
/// means the invoice had no drop-ship lines (most invoices); the field
/// is left null on the result in that case so existing callers don't
/// need to special-case it.
/// </summary>
public sealed record InvoiceDropShipCogsOutcome(
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    bool AlreadyPosted,
    decimal TotalAmountBase,
    string? ErrorMessage);
