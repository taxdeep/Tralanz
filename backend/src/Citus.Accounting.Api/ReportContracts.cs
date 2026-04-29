namespace Citus.Accounting.Api;

public sealed record TrialBalanceLookupQuery(
    Guid CompanyId,
    DateOnly? AsOfDate = null,
    bool IncludeZeroBalances = false);

public sealed record IncomeStatementLookupQuery(
    Guid CompanyId,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    bool IncludeZeroBalances = false);

public sealed record BalanceSheetLookupQuery(
    Guid CompanyId,
    DateOnly? AsOfDate = null,
    bool IncludeZeroBalances = false);

public sealed record ArAgingLookupQuery(
    Guid CompanyId,
    DateOnly? AsOfDate = null);

public sealed record ApAgingLookupQuery(
    Guid CompanyId,
    DateOnly? AsOfDate = null);

public sealed record SalesCashFlowLookupQuery(
    Guid CompanyId,
    DateOnly? AsOfDate = null);

public sealed record IncomeOverTimeLookupQuery(
    Guid CompanyId,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    bool CompareToPreviousYear = false);
