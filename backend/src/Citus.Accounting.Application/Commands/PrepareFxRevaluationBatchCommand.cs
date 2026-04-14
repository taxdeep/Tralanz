using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;

namespace Citus.Accounting.Application.Commands;

public sealed record PrepareFxRevaluationBatchCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid? BookId,
    DateOnly RevaluationDate,
    CurrencyCode TransactionCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    bool IncludeAccountsReceivable,
    bool IncludeAccountsPayable,
    string? Memo);

public sealed record PrepareFxRevaluationBatchCommandResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    Guid? BookId,
    string? BookCode,
    string? AccountingStandard,
    string? RevaluationProfile,
    string? FxRoundingPolicy,
    int PreparedLineCount,
    string Status)
{
    public static PrepareFxRevaluationBatchCommandResult FromRepositoryResult(
        FxRevaluationDraftPreparationResult result) =>
        new(
            result.DocumentId,
            result.EntityNumber,
            result.DisplayNumber,
            result.BookId,
            result.BookCode,
            result.AccountingStandard,
            result.RevaluationProfile,
            result.FxRoundingPolicy,
            result.PreparedLineCount,
            result.Status);
}
