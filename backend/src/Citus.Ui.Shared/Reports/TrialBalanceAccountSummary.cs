namespace Citus.Ui.Shared.Reports;

public sealed record class TrialBalanceAccountSummary
{
    public UserId AccountId { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string RootType { get; init; } = string.Empty;

    public string DetailType { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public bool IsSystem { get; init; }

    public decimal PostedDebitTotal { get; init; }

    public decimal PostedCreditTotal { get; init; }

    public decimal BalanceDebit { get; init; }

    public decimal BalanceCredit { get; init; }

    public decimal NetBalance { get; init; }

    public string BalanceSide { get; init; } = string.Empty;
}
