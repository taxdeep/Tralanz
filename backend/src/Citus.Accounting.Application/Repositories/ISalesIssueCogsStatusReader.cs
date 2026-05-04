using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// Read-side projection for the M3 iter 2 Sales Issue COGS workbench:
/// every posted sales-issue with its current bridge state — already
/// journalised (existing JE id + display number) or eligible for a
/// fresh post (estimated COGS amount rolled up from the cost-layer
/// consumptions). Crosses inventory + journal_entries so it lives
/// here rather than on either pure store interface.
/// </summary>
public interface ISalesIssueCogsStatusReader
{
    Task<IReadOnlyList<SalesIssueCogsStatusRow>> ListAsync(
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken);
}

public sealed record SalesIssueCogsStatusRow(
    Guid SalesIssueDocumentId,
    DateOnly PostingDate,
    string? SourceDocumentNumber,
    decimal EstimatedCogsBase,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber);
