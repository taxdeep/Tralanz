using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;

namespace Citus.Accounting.Application.Commands;

public sealed record PrepareFxRevaluationBatchCommand(
    CompanyId CompanyId,
    UserId UserId,
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
    int PreparedLineCount,
    string Status)
{
    public static PrepareFxRevaluationBatchCommandResult FromRepositoryResult(
        FxRevaluationDraftPreparationResult result) =>
        new(
            result.DocumentId,
            result.EntityNumber,
            result.DisplayNumber,
            result.PreparedLineCount,
            result.Status);
}
