using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Queries;

public sealed record class GetTrialBalanceQuery(
    CompanyId CompanyId,
    DateOnly AsOfDate,
    bool IncludeZeroBalanceAccounts = false);

public sealed record class TrialBalanceAccountBalance
{
    public Guid AccountId { get; init; }

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

    public string BalanceSide { get; init; } = "flat";

    public bool HasBalance => BalanceDebit != 0m || BalanceCredit != 0m;

    public static TrialBalanceAccountBalance Create(
        Guid accountId,
        string entityNumber,
        string code,
        string name,
        string rootType,
        string detailType,
        bool isActive,
        bool isSystem,
        decimal postedDebitTotal,
        decimal postedCreditTotal)
    {
        postedDebitTotal = Round6(postedDebitTotal);
        postedCreditTotal = Round6(postedCreditTotal);

        var netBalance = Round6(postedDebitTotal - postedCreditTotal);
        var balanceDebit = netBalance > 0m ? netBalance : 0m;
        var balanceCredit = netBalance < 0m ? Math.Abs(netBalance) : 0m;
        var balanceSide = netBalance > 0m
            ? "debit"
            : netBalance < 0m
                ? "credit"
                : "flat";

        return new TrialBalanceAccountBalance
        {
            AccountId = accountId,
            EntityNumber = entityNumber.Trim(),
            Code = code.Trim(),
            Name = name.Trim(),
            RootType = rootType.Trim(),
            DetailType = detailType.Trim(),
            IsActive = isActive,
            IsSystem = isSystem,
            PostedDebitTotal = postedDebitTotal,
            PostedCreditTotal = postedCreditTotal,
            BalanceDebit = balanceDebit,
            BalanceCredit = balanceCredit,
            NetBalance = netBalance,
            BalanceSide = balanceSide
        };
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}

public sealed record class TrialBalanceReport
{
    public Guid CompanyId { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool IncludeZeroBalanceAccounts { get; init; }

    public int AccountCount { get; init; }

    public decimal TotalBalanceDebit { get; init; }

    public decimal TotalBalanceCredit { get; init; }

    public bool IsBalanced { get; init; }

    public IReadOnlyList<TrialBalanceAccountBalance> Rows { get; init; } = Array.Empty<TrialBalanceAccountBalance>();

    public static TrialBalanceReport Create(
        Guid companyId,
        DateOnly asOfDate,
        string baseCurrencyCode,
        bool includeZeroBalanceAccounts,
        IEnumerable<TrialBalanceAccountBalance> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var orderedRows = rows
            .Where(row => includeZeroBalanceAccounts || row.HasBalance)
            .OrderBy(static row => row.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var totalBalanceDebit = Round6(orderedRows.Sum(static row => row.BalanceDebit));
        var totalBalanceCredit = Round6(orderedRows.Sum(static row => row.BalanceCredit));

        return new TrialBalanceReport
        {
            CompanyId = companyId,
            AsOfDate = asOfDate,
            BaseCurrencyCode = baseCurrencyCode.Trim().ToUpperInvariant(),
            IncludeZeroBalanceAccounts = includeZeroBalanceAccounts,
            AccountCount = orderedRows.Length,
            TotalBalanceDebit = totalBalanceDebit,
            TotalBalanceCredit = totalBalanceCredit,
            IsBalanced = totalBalanceDebit == totalBalanceCredit,
            Rows = orderedRows
        };
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
