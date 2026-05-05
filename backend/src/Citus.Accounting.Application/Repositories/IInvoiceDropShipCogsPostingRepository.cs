using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// M6 iter 3: drop-ship COGS recognition for posted invoices. Sister of
/// <see cref="ISalesIssueCogsPostingRepository"/>; the two coexist
/// because stock items earn their COGS via the inventory engine's cost
/// layers (Receipt → Issue → consumption rows) while drop-ship items
/// have no warehouse path and need a dedicated cost-basis lookup
/// (item.default_purchase_price).
/// </summary>
public interface IInvoiceDropShipCogsPostingRepository
{
    /// <summary>
    /// Returns null when an idempotency-probe hit shows the JE already
    /// exists. Returns null without an existing-id when the invoice has
    /// no drop-ship lines. Throws when an item is misconfigured (no
    /// purchase price, no resolvable COGS / clearing accounts) so the
    /// caller can surface the actionable error.
    /// </summary>
    Task<InvoiceDropShipCogsPostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId userId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken);
}

public sealed record InvoiceDropShipCogsPostingPreparation(
    InvoiceDropShipCogsPostingDocument? Document,
    Guid? ExistingJournalEntryId,
    string? ExistingJournalEntryDisplayNumber);
