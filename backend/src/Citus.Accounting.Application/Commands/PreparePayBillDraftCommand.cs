using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

public sealed record PreparePayBillDraftCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    Guid? AcceptedFxSnapshotId,
    string? Memo,
    IReadOnlyList<SettlementDraftLine> Lines,
    Guid? ClientRequestId = null);

public sealed record PreparePayBillDraftCommandResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    int PreparedLineCount,
    decimal TotalAmount,
    string Status)
{
    public static PreparePayBillDraftCommandResult FromRepositoryResult(
        SettlementDraftPreparationResult result) =>
        new(
            result.DocumentId,
            result.EntityNumber,
            result.DisplayNumber,
            result.PreparedLineCount,
            result.TotalAmount,
            result.Status);
}
