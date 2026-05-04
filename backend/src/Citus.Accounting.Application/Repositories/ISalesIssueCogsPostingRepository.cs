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
}

public sealed record SalesIssueCogsPostingPreparation(
    SalesIssueCogsPostingDocument? Document,
    Guid? ExistingJournalEntryId,
    string? ExistingJournalEntryDisplayNumber);
