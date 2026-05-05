namespace Citus.Accounting.Api;

public sealed record TrialBalanceLookupQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate = null,
    bool IncludeZeroBalances = false);

public sealed record IncomeStatementLookupQuery(
    CompanyId CompanyId,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    bool IncludeZeroBalances = false);

public sealed record BalanceSheetLookupQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate = null,
    bool IncludeZeroBalances = false);

public sealed record ArAgingLookupQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate = null);

public sealed record ApAgingLookupQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate = null);

public sealed record SalesCashFlowLookupQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate = null);

public sealed record IncomeOverTimeLookupQuery(
    CompanyId CompanyId,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    bool CompareToPreviousYear = false);

public sealed record ExpenseCashOutflowLookupQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate = null);

public sealed record ExpenseOverTimeLookupQuery(
    CompanyId CompanyId,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    bool CompareToPreviousYear = false);
