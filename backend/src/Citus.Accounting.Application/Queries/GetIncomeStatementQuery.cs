using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Queries;

public sealed record class GetIncomeStatementQuery(
    CompanyId CompanyId,
    DateOnly DateFrom,
    DateOnly DateTo,
    bool IncludeZeroBalanceAccounts = false);

public sealed record class IncomeStatementAccountAmount
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

    public decimal DisplayAmount { get; init; }

    public bool HasActivity => PostedDebitTotal != 0m || PostedCreditTotal != 0m || DisplayAmount != 0m;

    public static IncomeStatementAccountAmount Create(
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
            "revenue" => Round6(postedCreditTotal - postedDebitTotal),
            "cost_of_sales" => Round6(postedDebitTotal - postedCreditTotal),
            "expense" => Round6(postedDebitTotal - postedCreditTotal),
            _ => throw new InvalidOperationException(
                $"Account root type '{normalizedRootType}' is not legal in an income statement.")
        };

        return new IncomeStatementAccountAmount
        {
            AccountId = accountId,
            EntityNumber = entityNumber.Trim(),
            Code = code.Trim(),
            Name = name.Trim(),
            RootType = normalizedRootType,
            DetailType = detailType.Trim(),
            IsActive = isActive,
            IsSystem = isSystem,
            PostedDebitTotal = postedDebitTotal,
            PostedCreditTotal = postedCreditTotal,
            DisplayAmount = displayAmount
        };
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}

public sealed record class IncomeStatementReport
{
    public Guid CompanyId { get; init; }

    public DateOnly DateFrom { get; init; }

    public DateOnly DateTo { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool IncludeZeroBalanceAccounts { get; init; }

    public int AccountCount { get; init; }

    public decimal TotalRevenue { get; init; }

    public decimal TotalCostOfSales { get; init; }

    public decimal GrossProfit { get; init; }

    public decimal TotalExpenses { get; init; }

    public decimal NetIncome { get; init; }

    public IReadOnlyList<IncomeStatementAccountAmount> RevenueRows { get; init; } = Array.Empty<IncomeStatementAccountAmount>();

    public IReadOnlyList<IncomeStatementAccountAmount> CostOfSalesRows { get; init; } = Array.Empty<IncomeStatementAccountAmount>();

    public IReadOnlyList<IncomeStatementAccountAmount> ExpenseRows { get; init; } = Array.Empty<IncomeStatementAccountAmount>();

    public static IncomeStatementReport Create(
        Guid companyId,
        DateOnly dateFrom,
        DateOnly dateTo,
        string baseCurrencyCode,
        bool includeZeroBalanceAccounts,
        IEnumerable<IncomeStatementAccountAmount> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var filteredRows = rows
            .Where(row => includeZeroBalanceAccounts || row.HasActivity)
            .OrderBy(static row => row.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var revenueRows = filteredRows.Where(static row => row.RootType == "revenue").ToArray();
        var costOfSalesRows = filteredRows.Where(static row => row.RootType == "cost_of_sales").ToArray();
        var expenseRows = filteredRows.Where(static row => row.RootType == "expense").ToArray();

        var totalRevenue = Round6(revenueRows.Sum(static row => row.DisplayAmount));
        var totalCostOfSales = Round6(costOfSalesRows.Sum(static row => row.DisplayAmount));
        var grossProfit = Round6(totalRevenue - totalCostOfSales);
        var totalExpenses = Round6(expenseRows.Sum(static row => row.DisplayAmount));
        var netIncome = Round6(grossProfit - totalExpenses);

        return new IncomeStatementReport
        {
            CompanyId = companyId,
            DateFrom = dateFrom,
            DateTo = dateTo,
            BaseCurrencyCode = baseCurrencyCode.Trim().ToUpperInvariant(),
            IncludeZeroBalanceAccounts = includeZeroBalanceAccounts,
            AccountCount = filteredRows.Length,
            TotalRevenue = totalRevenue,
            TotalCostOfSales = totalCostOfSales,
            GrossProfit = grossProfit,
            TotalExpenses = totalExpenses,
            NetIncome = netIncome,
            RevenueRows = revenueRows,
            CostOfSalesRows = costOfSalesRows,
            ExpenseRows = expenseRows
        };
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
