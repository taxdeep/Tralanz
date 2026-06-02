using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// Reads the original posted bill journal entry (source_type='bill',
/// source_id=billId) plus its journal_entry_lines, then assembles a
/// pre-flipped <see cref="BillReversePostingDocument"/> the engine can
/// dispatch to <c>BuildBillReverseFragments</c>. Idempotent: if a JE with
/// source_type='bill_reversal' + source_id=billId already exists, returns it
/// without rebuilding the document. Mirror of
/// <see cref="IInvoiceReversePostingRepository"/>.
/// </summary>
public interface IBillReversePostingRepository
{
    Task<BillReversePostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid billId,
        CancellationToken cancellationToken);
}

public sealed record BillReversePostingPreparation(
    BillReversePostingDocument? Document,
    Guid? ExistingJournalEntryId,
    string? ExistingJournalEntryDisplayNumber);
