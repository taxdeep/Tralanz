using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application.Repositories;

public interface IReceiptGrIrSettlementPostingRepository
{
    Task<ReceiptGrIrSettlementPostingDocument> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken);

    Task CompletePostingAsync(
        CompanyId companyId,
        UserId userId,
        Guid settlementBatchId,
        Guid journalEntryId,
        string journalEntryDisplayNumber,
        CancellationToken cancellationToken);
}
