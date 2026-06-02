using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// Reads the original posted invoice journal entry (source_type='invoice',
/// source_id=invoiceId) plus its journal_entry_lines, then assembles a
/// pre-flipped <see cref="InvoiceReversePostingDocument"/> the engine can
/// dispatch to <c>BuildInvoiceReverseFragments</c>. Idempotent: if a JE
/// with source_type='invoice_reversal' + source_id=invoiceId already
/// exists, returns it without rebuilding the document.
/// </summary>
public interface IInvoiceReversePostingRepository
{
    Task<InvoiceReversePostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid invoiceId,
        CancellationToken cancellationToken);
}

public sealed record InvoiceReversePostingPreparation(
    InvoiceReversePostingDocument? Document,
    Guid? ExistingJournalEntryId,
    string? ExistingJournalEntryDisplayNumber);
