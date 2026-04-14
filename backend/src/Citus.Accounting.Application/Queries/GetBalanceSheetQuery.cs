using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Queries;

public sealed record class GetBalanceSheetQuery(
    CompanyId CompanyId,
    DateOnly AsOfDate,
    bool IncludeZeroBalanceAccounts = false);

public sealed record class BalanceSheetAccountAmount
{
    public Guid? AccountId { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string RootType { get; init; } = string.Empty;

    public string DetailType { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public bool IsSystem { get; init; }

    public bool IsSynthetic { get; init; }

    public decimal PostedDebitTotal { get; init; }

    public decimal PostedCreditTotal { get; init; }

    public decimal DisplayAmount { get; init; }

    public bool HasBalance => DisplayAmount != 0m || PostedDebitTotal != 0m || PostedCreditTotal != 0m;

    public static BalanceSheetAccountAmount Create(
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

        var normalizedRootType = rootType.Trim();
        var displayAmount = normalizedRootType switch
        {
            "asset" => Round6(postedDebitTotal - postedCreditTotal),
            "liability" => Round6(postedCreditTotal - postedDebitTotal),
            "equity" => Round6(postedCreditTotal - postedDebitTotal),
            _ => throw new InvalidOperationException(
                $"Account root type '{normalizedRootType}' is not legal in a balance sheet.")
        };

        return new BalanceSheetAccountAmount
        {
            AccountId = accountId,
            EntityNumber = entityNumber.Trim(),
            Code = code.Trim(),
            Name = name.Trim(),
            RootType = normalizedRootType,
            DetailType = detailType.Trim(),
            IsActive = isActive,
            IsSystem = isSystem,
            IsSynthetic = false,
            PostedDebitTotal = postedDebitTotal,
            PostedCreditTotal = postedCreditTotal,
            DisplayAmount = displayAmount
        };
    }

    public static BalanceSheetAccountAmount CreateSyntheticCurrentEarnings(decimal currentEarnings)
    {
        currentEarnings = Round6(currentEarnings);

        return new BalanceSheetAccountAmount
        {
            AccountId = null,
            EntityNumber = "SYNTHETIC-CURRENT-EARNINGS",
            Code = "CURRENT-EARNINGS",
            Name = "Current Earnings",
            RootType = "equity",
            DetailType = "synthetic_current_earnings",
            IsActive = true,
            IsSystem = true,
            IsSynthetic = true,
            PostedDebitTotal = currentEarnings < 0m ? Math.Abs(currentEarnings) : 0m,
            PostedCreditTotal = currentEarnings > 0m ? currentEarnings : 0m,
            DisplayAmount = currentEarnings
        };
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}

public sealed record class BalanceSheetReport
{
    public Guid CompanyId { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool IncludeZeroBalanceAccounts { get; init; }

    public int AccountCount { get; init; }

    public decimal TotalAssets { get; init; }

    public decimal TotalLiabilities { get; init; }

    public decimal CurrentEarnings { get; init; }

    public decimal TotalEquity { get; init; }

    public decimal TotalLiabilitiesAndEquity { get; init; }

    public bool IsBalanced { get; init; }

    public IReadOnlyList<BalanceSheetAccountAmount> AssetRows { get; init; } = Array.Empty<BalanceSheetAccountAmount>();

    public IReadOnlyList<BalanceSheetAccountAmount> LiabilityRows { get; init; } = Array.Empty<BalanceSheetAccountAmount>();

    public IReadOnlyList<BalanceSheetAccountAmount> EquityRows { get; init; } = Array.Empty<BalanceSheetAccountAmount>();

    public static BalanceSheetReport Create(
        Guid companyId,
        DateOnly asOfDate,
        string baseCurrencyCode,
        bool includeZeroBalanceAccounts,
        IEnumerable<BalanceSheetAccountAmount> rows,
        decimal currentEarnings)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var filteredRows = rows
            .Where(row => includeZeroBalanceAccounts || row.HasBalance)
            .OrderBy(static row => row.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var assetRows = filteredRows.Where(static row => row.RootType == "asset").ToArray();
        var liabilityRows = filteredRows.Where(static row => row.RootType == "liability").ToArray();

        var equityRows = filteredRows
            .Where(static row => row.RootType == "equity")
            .ToList();

        var currentEarningsRow = BalanceSheetAccountAmount.CreateSyntheticCurrentEarnings(currentEarnings);
        if (includeZeroBalanceAccounts || currentEarningsRow.HasBalance)
        {
            equityRows.Add(currentEarningsRow);
        }

        var orderedEquityRows = equityRows
            .OrderBy(static row => row.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var totalAssets = Round6(assetRows.Sum(static row => row.DisplayAmount));
        var totalLiabilities = Round6(liabilityRows.Sum(static row => row.DisplayAmount));
        var totalEquity = Round6(orderedEquityRows.Sum(static row => row.DisplayAmount));
        var totalLiabilitiesAndEquity = Round6(totalLiabilities + totalEquity);

        return new BalanceSheetReport
        {
            CompanyId = companyId,
            AsOfDate = asOfDate,
            BaseCurrencyCode = baseCurrencyCode.Trim().ToUpperInvariant(),
            IncludeZeroBalanceAccounts = includeZeroBalanceAccounts,
            AccountCount = assetRows.Length + liabilityRows.Length + orderedEquityRows.Length,
            TotalAssets = totalAssets,
            TotalLiabilities = totalLiabilities,
            CurrentEarnings = Round6(currentEarningsRow.DisplayAmount),
            TotalEquity = totalEquity,
            TotalLiabilitiesAndEquity = totalLiabilitiesAndEquity,
            IsBalanced = totalAssets == totalLiabilitiesAndEquity,
            AssetRows = assetRows,
            LiabilityRows = liabilityRows,
            EquityRows = orderedEquityRows
        };
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
