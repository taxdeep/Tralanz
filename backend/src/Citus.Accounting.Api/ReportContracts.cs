namespace Citus.Accounting.Api;

public sealed record TrialBalanceLookupQuery(
    CompanyId CompanyId,
    DateOnly? AsOfDate = null,
    bool IncludeZeroBalances = false);

public sealed record IncomeStatementLookupQuery(
    CompanyId CompanyId,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    bool IncludeZeroBalances = false,
    string? Basis = null);

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

// Body for the statement-send endpoints. ToEmail is optional — blank falls
// back to the party's email on file.
public sealed record StatementSendHttpRequest(
    string? ToEmail,
    string? Cc,
    string? Bcc,
    string? Message);

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
