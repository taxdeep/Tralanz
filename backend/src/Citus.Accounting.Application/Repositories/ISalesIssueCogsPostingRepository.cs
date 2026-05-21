using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// Reads sales-issue inventory truth (consumption rows × cost layers ×
/// item account defaults × company SystemRole fallbacks) and assembles
/// a <see cref="SalesIssueCogsPostingDocument"/> the posting engine
/// can journalise. Mirror of <see cref="IReceiptGrIrPostingRepository"/>
/// but for the outbound (Dr COGS / Cr Inventory) leg.
/// </summary>
public interface ISalesIssueCogsPostingRepository
{
    /// <summary>
    /// Returns null if the sales-issue has already produced a COGS
    /// journal entry (idempotency probe via journal_entries source_type +
    /// source_id). Otherwise returns the prepared posting document.
    /// Throws if the sales-issue is missing, not posted, or has no
    /// consumable layer rows.
    /// </summary>
    Task<SalesIssueCogsPostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId userId,
        Guid salesIssueDocumentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// P0-2 (C2): builds the compensating COGS posting document for an
    /// invoice-reverse run. Reads the same consumption rows as the
    /// forward prepare but flags <see cref="SalesIssueCogsPostingDocument.IsReverse"/>
    /// = true so the fragment builder swaps debits and credits.
    ///
    /// Idempotency: probes for an existing JE with
    /// <c>source_type='sales_issue_cogs_reverse'</c> and
    /// <c>source_id=salesIssueId</c>; returns Document=null when found
    /// so the caller surfaces the prior result instead of double-posting.
    ///
    /// If the forward COGS JE was never posted (rare — only when
    /// PostInvoiceCommandHandler's soft-failure Step 2 swallowed an
    /// error), the preparation surfaces <see cref="SalesIssueCogsReversePostingPreparation.ForwardNotPosted"/>
    /// = true. Callers skip the reverse-GL step in that case; the
    /// inventory subledger reverse still runs.
    /// </summary>
    Task<SalesIssueCogsReversePostingPreparation> PrepareReversePostingDocumentAsync(
        CompanyId companyId,
        UserId userId,
        Guid salesIssueDocumentId,
        CancellationToken cancellationToken);
}

public sealed record SalesIssueCogsPostingPreparation(
    SalesIssueCogsPostingDocument? Document,
    Guid? ExistingJournalEntryId,
    string? ExistingJournalEntryDisplayNumber);

public sealed record SalesIssueCogsReversePostingPreparation(
    SalesIssueCogsPostingDocument? Document,
    Guid? ExistingReverseJournalEntryId,
    string? ExistingReverseJournalEntryDisplayNumber,
    bool ForwardNotPosted);
