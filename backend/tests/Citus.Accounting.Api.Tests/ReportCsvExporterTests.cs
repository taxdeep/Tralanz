using Citus.Ui.Shared.Reports;

namespace Citus.Accounting.Api.Tests;

public sealed class ReportCsvExporterTests
{
    [Fact]
    public void ExportTrialBalance_WritesMetadataAndAccountRows()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var report = new TrialBalanceReportSummary
        {
            CompanyId = companyId,
            AsOfDate = new DateOnly(2026, 4, 13),
            BaseCurrencyCode = "USD",
            IncludeZeroBalanceAccounts = true,
            AccountCount = 1,
            TotalBalanceDebit = 1750m,
            TotalBalanceCredit = 1750m,
            IsBalanced = true,
            Rows =
            [
                new TrialBalanceAccountSummary
                {
                    AccountId = Guid.NewGuid(),
                    EntityNumber = "EN20260000A",
                    Code = "1010",
                    Name = "Cash",
                    RootType = "asset",
                    DetailType = "cash",
                    IsActive = true,
                    IsSystem = false,
                    PostedDebitTotal = 1750m,
                    PostedCreditTotal = 0m,
                    BalanceDebit = 1750m,
                    BalanceCredit = 0m,
                    NetBalance = 1750m,
                    BalanceSide = "debit"
                }
            ]
        };

        var file = ReportCsvExporter.ExportTrialBalance(report);

        Assert.Equal("trial-balance-2026-04-13.csv", file.FileName);
        Assert.Contains("Report,Trial Balance", file.Content);
        Assert.Contains("TotalBalanceDebit,1750.00", file.Content);
        Assert.Contains("Section,Accounts", file.Content);
        Assert.Contains("1010,Cash,asset,cash,EN20260000A,Active,false,1750.00,0.00,1750.00,0.00,1750.00,debit", file.Content);
    }

    [Fact]
    public void ExportIncomeStatement_WritesSectionTotals()
    {
        var report = new IncomeStatementReportSummary
        {
            CompanyId = CompanyId.FromOrdinal(1),
            DateFrom = new DateOnly(2026, 4, 1),
            DateTo = new DateOnly(2026, 4, 30),
            BaseCurrencyCode = "USD",
            AccountCount = 2,
            TotalRevenue = 900m,
            TotalCostOfSales = 250m,
            GrossProfit = 650m,
            TotalExpenses = 200m,
            NetIncome = 450m,
            RevenueRows =
            [
                new IncomeStatementAccountSummary
                {
                    AccountId = Guid.NewGuid(),
                    EntityNumber = "EN20260000A",
                    Code = "4000",
                    Name = "Service Revenue",
                    RootType = "revenue",
                    DetailType = "service_revenue",
                    IsActive = true,
                    IsSystem = false,
                    PostedDebitTotal = 0m,
                    PostedCreditTotal = 900m,
                    DisplayAmount = 900m
                }
            ],
            ExpenseRows =
            [
                new IncomeStatementAccountSummary
                {
                    AccountId = Guid.NewGuid(),
                    EntityNumber = "EN20260000A",
                    Code = "6100",
                    Name = "Office Expense",
                    RootType = "expense",
                    DetailType = "office_expense",
                    IsActive = true,
                    IsSystem = false,
                    PostedDebitTotal = 200m,
                    PostedCreditTotal = 0m,
                    DisplayAmount = 200m
                }
            ]
        };

        var file = ReportCsvExporter.ExportIncomeStatement(report);

        Assert.Equal("income-statement-2026-04-01-to-2026-04-30.csv", file.FileName);
        Assert.Contains("NetIncome,450.00", file.Content);
        Assert.Contains("Section,Revenue", file.Content);
        Assert.Contains("SectionTotal,Revenue,900.00", file.Content);
        Assert.Contains("Section,Expenses", file.Content);
        Assert.Contains("6100,Office Expense,expense,office_expense,EN20260000A,Active,false,200.00,0.00,200.00", file.Content);
    }

    [Fact]
    public void ExportBalanceSheet_WritesSyntheticRowsAndTotals()
    {
        var report = new BalanceSheetReportSummary
        {
            CompanyId = CompanyId.FromOrdinal(1),
            AsOfDate = new DateOnly(2026, 4, 30),
            BaseCurrencyCode = "USD",
            AccountCount = 2,
            TotalAssets = 2200m,
            TotalLiabilities = 0m,
            CurrentEarnings = 450m,
            TotalEquity = 2200m,
            TotalLiabilitiesAndEquity = 2200m,
            IsBalanced = true,
            AssetRows =
            [
                new BalanceSheetAccountSummary
                {
                    AccountId = Guid.NewGuid(),
                    EntityNumber = "EN20260000A",
                    Code = "1010",
                    Name = "Cash",
                    RootType = "asset",
                    DetailType = "cash",
                    IsActive = true,
                    IsSystem = false,
                    IsSynthetic = false,
                    PostedDebitTotal = 2200m,
                    PostedCreditTotal = 0m,
                    DisplayAmount = 2200m
                }
            ],
            EquityRows =
            [
                new BalanceSheetAccountSummary
                {
                    AccountId = null,
                    EntityNumber = "SYSTEM",
                    Code = "CURRENT-EARNINGS",
                    Name = "Current Earnings",
                    RootType = "equity",
                    DetailType = "current_earnings",
                    IsActive = true,
                    IsSystem = true,
                    IsSynthetic = true,
                    PostedDebitTotal = 0m,
                    PostedCreditTotal = 450m,
                    DisplayAmount = 450m
                }
            ]
        };

        var file = ReportCsvExporter.ExportBalanceSheet(report);

        Assert.Equal("balance-sheet-2026-04-30.csv", file.FileName);
        Assert.Contains("CurrentEarnings,450.00", file.Content);
        Assert.Contains("Section,Equity", file.Content);
        Assert.Contains("CURRENT-EARNINGS,Current Earnings,equity,current_earnings,SYSTEM,Active,true,true,0.00,450.00,450.00", file.Content);
        Assert.Contains("SectionTotal,Assets,2200.00", file.Content);
    }

    [Fact]
    public void ExportArAging_WritesCustomerSummaryAndDetailRows()
    {
        var customerId = Guid.NewGuid();
        var report = new ArAgingReportSummary
        {
            CompanyId = CompanyId.FromOrdinal(1),
            AsOfDate = new DateOnly(2026, 4, 13),
            BaseCurrencyCode = "USD",
            CustomerCount = 1,
            OpenItemCount = 1,
            CurrentAmountBase = 525m,
            TotalOutstandingAmountBase = 525m,
            CustomerRows =
            [
                new ArAgingCustomerSummary
                {
                    CustomerId = customerId,
                    CustomerEntityNumber = "EN20260000A",
                    CustomerDisplayName = "Acme Retail",
                    CustomerIsActive = true,
                    OpenItemCount = 1,
                    OldestDueDate = new DateOnly(2026, 4, 30),
                    CurrentAmountBase = 525m,
                    TotalOutstandingAmountBase = 525m
                }
            ],
            DetailRows =
            [
                new ArAgingOpenItemSummary
                {
                    OpenItemId = Guid.NewGuid(),
                    CustomerId = customerId,
                    CustomerEntityNumber = "EN20260000A",
                    CustomerDisplayName = "Acme Retail",
                    CustomerIsActive = true,
                    SourceType = "invoice",
                    SourceDocumentId = Guid.NewGuid(),
                    DisplayNumber = "INV-AR-1001",
                    DocumentDate = new DateOnly(2026, 4, 1),
                    DueDate = new DateOnly(2026, 4, 30),
                    DaysPastDue = 0,
                    AgingBucket = "current",
                    DocumentCurrencyCode = "USD",
                    BaseCurrencyCode = "USD",
                    BalanceSide = "debit",
                    Status = "open",
                    OriginalAmountTx = 525m,
                    OriginalAmountBase = 525m,
                    OpenAmountTx = 525m,
                    OpenAmountBase = 525m,
                    SignedOpenAmountTx = 525m,
                    SignedOpenAmountBase = 525m
                }
            ]
        };

        var file = ReportCsvExporter.ExportArAging(report);

        Assert.Equal("ar-aging-2026-04-13.csv", file.FileName);
        Assert.Contains("Report,A/R Aging", file.Content);
        Assert.Contains("Section,Customer Summary", file.Content);
        Assert.Contains("EN20260000A,Acme Retail,Active,1,2026-04-30,525.00,0.00,0.00,0.00,0.00,0.00,525.00", file.Content);
        Assert.Contains("Section,Open Item Detail", file.Content);
        Assert.Contains("EN20260000A,Acme Retail,Active,invoice,INV-AR-1001,2026-04-01,2026-04-30,0,current,USD,USD,debit,open,525.00,525.00,525.00,525.00,525.00,525.00", file.Content);
    }

    [Fact]
    public void ExportApAging_WritesVendorSummaryAndDetailRows()
    {
        var vendorId = Guid.NewGuid();
        var report = new ApAgingReportSummary
        {
            CompanyId = CompanyId.FromOrdinal(1),
            AsOfDate = new DateOnly(2026, 4, 13),
            BaseCurrencyCode = "USD",
            VendorCount = 1,
            OpenItemCount = 1,
            DaysOver90AmountBase = 1100m,
            TotalOverdueAmountBase = 1100m,
            TotalOutstandingAmountBase = 1100m,
            VendorRows =
            [
                new ApAgingVendorSummary
                {
                    VendorId = vendorId,
                    VendorEntityNumber = "EN20260000A",
                    VendorDisplayName = "North Harbor Supply",
                    VendorIsActive = true,
                    OpenItemCount = 1,
                    OldestDueDate = new DateOnly(2025, 12, 31),
                    DaysOver90AmountBase = 1100m,
                    TotalOverdueAmountBase = 1100m,
                    TotalOutstandingAmountBase = 1100m
                }
            ],
            DetailRows =
            [
                new ApAgingOpenItemSummary
                {
                    OpenItemId = Guid.NewGuid(),
                    VendorId = vendorId,
                    VendorEntityNumber = "EN20260000A",
                    VendorDisplayName = "North Harbor Supply",
                    VendorIsActive = true,
                    SourceType = "bill",
                    SourceDocumentId = Guid.NewGuid(),
                    DisplayNumber = "BILL-AP-1004",
                    DocumentDate = new DateOnly(2025, 12, 1),
                    DueDate = new DateOnly(2025, 12, 31),
                    DaysPastDue = 103,
                    AgingBucket = "over_90",
                    DocumentCurrencyCode = "USD",
                    BaseCurrencyCode = "USD",
                    BalanceSide = "credit",
                    Status = "open",
                    OriginalAmountTx = 1100m,
                    OriginalAmountBase = 1100m,
                    OpenAmountTx = 1100m,
                    OpenAmountBase = 1100m,
                    SignedOpenAmountTx = 1100m,
                    SignedOpenAmountBase = 1100m
                }
            ]
        };

        var file = ReportCsvExporter.ExportApAging(report);

        Assert.Equal("ap-aging-2026-04-13.csv", file.FileName);
        Assert.Contains("Report,A/P Aging", file.Content);
        Assert.Contains("Section,Vendor Summary", file.Content);
        Assert.Contains("EN20260000A,North Harbor Supply,Active,1,2025-12-31,0.00,0.00,0.00,0.00,1100.00,1100.00,1100.00", file.Content);
        Assert.Contains("Section,Open Item Detail", file.Content);
        Assert.Contains("EN20260000A,North Harbor Supply,Active,bill,BILL-AP-1004,2025-12-01,2025-12-31,103,over_90,USD,USD,credit,open,1100.00,1100.00,1100.00,1100.00,1100.00,1100.00", file.Content);
    }
}
