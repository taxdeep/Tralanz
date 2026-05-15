using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application.Repositories;

public interface IReceiptGrIrPostingRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<ReceiptGrIrPostingDocument> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        Guid grIrClearingAccountId,
        CancellationToken cancellationToken);

    Task CompletePostingAsync(
        CompanyId companyId,
        UserId userId,
        Guid postingBatchId,
        Guid journalEntryId,
        string journalEntryDisplayNumber,
        CancellationToken cancellationToken);
}
