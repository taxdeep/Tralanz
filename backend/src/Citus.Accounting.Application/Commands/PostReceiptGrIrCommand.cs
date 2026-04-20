using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed record PostReceiptGrIrCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid ReceiptDocumentId,
    Guid? GrIrClearingAccountId,
    string? IdempotencyKey);

public sealed record PostReceiptGrIrCommandResult(
    Guid ReceiptDocumentId,
    Guid PostingBatchId,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt)
{
    public static PostReceiptGrIrCommandResult FromPostingResult(
        Guid receiptDocumentId,
        Guid postingBatchId,
        PostingResult result) =>
        new(
            receiptDocumentId,
            postingBatchId,
            result.JournalEntryId,
            result.JournalEntryDisplayNumber,
            result.Status,
            result.PostedAt);
}
