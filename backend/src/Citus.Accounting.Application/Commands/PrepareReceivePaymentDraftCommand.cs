using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

public sealed record PrepareReceivePaymentDraftCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    Guid? AcceptedFxSnapshotId,
    string? Memo,
    IReadOnlyList<SettlementDraftLine> Lines,
    /// <summary>Overpayment slice parked as a Customer Deposit. See
    /// <see cref="ReceivePaymentDraftPreparation.ExtraDepositAmount"/>.</summary>
    decimal ExtraDepositAmount = 0m);

public sealed record PrepareReceivePaymentDraftCommandResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    int PreparedLineCount,
    decimal TotalAmount,
    string Status)
{
    public static PrepareReceivePaymentDraftCommandResult FromRepositoryResult(
        SettlementDraftPreparationResult result) =>
        new(
            result.DocumentId,
            result.EntityNumber,
            result.DisplayNumber,
            result.PreparedLineCount,
            result.TotalAmount,
            result.Status);
}
