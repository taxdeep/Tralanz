using System.Globalization;
using System.Text;
using Citus.Ui.Shared.Reports;

namespace Citus.Accounting.Api;

public static class ReportCsvExporter
{
    public static ReportCsvFile ExportTrialBalance(TrialBalanceReportSummary report)
    {
        var builder = new CsvBuilder();

        AppendMetadata(
            builder,
            "Trial Balance",
            ("CompanyId", report.CompanyId),
            ("AsOfDate", report.AsOfDate),
            ("BaseCurrencyCode", report.BaseCurrencyCode),
            ("IncludeZeroBalanceAccounts", report.IncludeZeroBalanceAccounts),
            ("AccountCount", report.AccountCount),
            ("TotalBalanceDebit", report.TotalBalanceDebit),
            ("TotalBalanceCredit", report.TotalBalanceCredit),
            ("IsBalanced", report.IsBalanced));

        builder.AppendBlankLine();
        builder.AppendRow("Section", "Accounts");
        builder.AppendRow(
            "Code",
            "Name",
            "RootType",
            "DetailType",
            "EntityNumber",
            "Status",
            "System",
            "PostedDebitTotal",
            "PostedCreditTotal",
            "BalanceDebit",
            "BalanceCredit",
            "NetBalance",
            "BalanceSide");

        foreach (var row in report.Rows)
        {
            builder.AppendRow(
                row.Code,
                row.Name,
                row.RootType,
                row.DetailType,
                row.EntityNumber,
                row.IsActive ? "Active" : "Inactive",
                row.IsSystem,
                row.PostedDebitTotal,
                row.PostedCreditTotal,
                row.BalanceDebit,
                row.BalanceCredit,
                row.NetBalance,
                row.BalanceSide);
        }

        return new ReportCsvFile(
            $"trial-balance-{report.AsOfDate:yyyy-MM-dd}.csv",
            builder.ToString());
    }

    public static ReportCsvFile ExportIncomeStatement(IncomeStatementReportSummary report)
    {
        var builder = new CsvBuilder();

        AppendMetadata(
            builder,
            "Income Statement",
            ("CompanyId", report.CompanyId),
            ("DateFrom", report.DateFrom),
            ("DateTo", report.DateTo),
            ("BaseCurrencyCode", report.BaseCurrencyCode),
            ("IncludeZeroBalanceAccounts", report.IncludeZeroBalanceAccounts),
            ("AccountCount", report.AccountCount),
            ("TotalRevenue", report.TotalRevenue),
            ("TotalCostOfSales", report.TotalCostOfSales),
            ("GrossProfit", report.GrossProfit),
            ("TotalExpenses", report.TotalExpenses),
            ("NetIncome", report.NetIncome));

        AppendIncomeSection(builder, "Revenue", report.RevenueRows, report.TotalRevenue);
        AppendIncomeSection(builder, "Cost Of Sales", report.CostOfSalesRows, report.TotalCostOfSales);
        AppendIncomeSection(builder, "Expenses", report.ExpenseRows, report.TotalExpenses);

        return new ReportCsvFile(
            $"income-statement-{report.DateFrom:yyyy-MM-dd}-to-{report.DateTo:yyyy-MM-dd}.csv",
            builder.ToString());
    }

    public static ReportCsvFile ExportBalanceSheet(BalanceSheetReportSummary report)
    {
        var builder = new CsvBuilder();

        AppendMetadata(
            builder,
            "Balance Sheet",
            ("CompanyId", report.CompanyId),
            ("AsOfDate", report.AsOfDate),
            ("BaseCurrencyCode", report.BaseCurrencyCode),
            ("IncludeZeroBalanceAccounts", report.IncludeZeroBalanceAccounts),
            ("AccountCount", report.AccountCount),
            ("TotalAssets", report.TotalAssets),
            ("TotalLiabilities", report.TotalLiabilities),
            ("CurrentEarnings", report.CurrentEarnings),
            ("TotalEquity", report.TotalEquity),
            ("TotalLiabilitiesAndEquity", report.TotalLiabilitiesAndEquity),
            ("IsBalanced", report.IsBalanced));

        AppendBalanceSection(builder, "Assets", report.AssetRows, report.TotalAssets);
        AppendBalanceSection(builder, "Liabilities", report.LiabilityRows, report.TotalLiabilities);
        AppendBalanceSection(builder, "Equity", report.EquityRows, report.TotalEquity);

        return new ReportCsvFile(
            $"balance-sheet-{report.AsOfDate:yyyy-MM-dd}.csv",
            builder.ToString());
    }

    public static ReportCsvFile ExportArAging(ArAgingReportSummary report)
    {
        var builder = new CsvBuilder();

        AppendAgingMetadata(
            builder,
            "A/R Aging",
            report.CompanyId,
            report.AsOfDate,
            report.BaseCurrencyCode,
            report.CustomerCount,
            report.OpenItemCount,
            report.CurrentAmountBase,
            report.Days1To30AmountBase,
            report.Days31To60AmountBase,
            report.Days61To90AmountBase,
            report.DaysOver90AmountBase,
            report.TotalOverdueAmountBase,
            report.TotalOutstandingAmountBase);

        builder.AppendBlankLine();
        builder.AppendRow("Section", "Customer Summary");
        builder.AppendRow(
            "CustomerEntityNumber",
            "CustomerDisplayName",
            "Status",
            "OpenItemCount",
            "OldestDueDate",
            "CurrentAmountBase",
            "Days1To30AmountBase",
            "Days31To60AmountBase",
            "Days61To90AmountBase",
            "DaysOver90AmountBase",
            "TotalOverdueAmountBase",
            "TotalOutstandingAmountBase");

        foreach (var row in report.CustomerRows)
        {
            builder.AppendRow(
                row.CustomerEntityNumber,
                row.CustomerDisplayName,
                row.CustomerIsActive ? "Active" : "Inactive",
                row.OpenItemCount,
                row.OldestDueDate,
                row.CurrentAmountBase,
                row.Days1To30AmountBase,
                row.Days31To60AmountBase,
                row.Days61To90AmountBase,
                row.DaysOver90AmountBase,
                row.TotalOverdueAmountBase,
                row.TotalOutstandingAmountBase);
        }

        builder.AppendBlankLine();
        builder.AppendRow("Section", "Open Item Detail");
        builder.AppendRow(
            "CustomerEntityNumber",
            "CustomerDisplayName",
            "Status",
            "SourceType",
            "DisplayNumber",
            "DocumentDate",
            "DueDate",
            "DaysPastDue",
            "AgingBucket",
            "DocumentCurrencyCode",
            "BaseCurrencyCode",
            "BalanceSide",
            "OpenStatus",
            "OriginalAmountTx",
            "OriginalAmountBase",
            "OpenAmountTx",
            "OpenAmountBase",
            "SignedOpenAmountTx",
            "SignedOpenAmountBase");

        foreach (var row in report.DetailRows)
        {
            builder.AppendRow(
                row.CustomerEntityNumber,
                row.CustomerDisplayName,
                row.CustomerIsActive ? "Active" : "Inactive",
                row.SourceType,
                row.DisplayNumber,
                row.DocumentDate,
                row.DueDate,
                row.DaysPastDue,
                row.AgingBucket,
                row.DocumentCurrencyCode,
                row.BaseCurrencyCode,
                row.BalanceSide,
                row.Status,
                row.OriginalAmountTx,
                row.OriginalAmountBase,
                row.OpenAmountTx,
                row.OpenAmountBase,
                row.SignedOpenAmountTx,
                row.SignedOpenAmountBase);
        }

        return new ReportCsvFile(
            $"ar-aging-{report.AsOfDate:yyyy-MM-dd}.csv",
            builder.ToString());
    }

    public static ReportCsvFile ExportApAging(ApAgingReportSummary report)
    {
        var builder = new CsvBuilder();

        AppendAgingMetadata(
            builder,
            "A/P Aging",
            report.CompanyId,
            report.AsOfDate,
            report.BaseCurrencyCode,
            report.VendorCount,
            report.OpenItemCount,
            report.CurrentAmountBase,
            report.Days1To30AmountBase,
            report.Days31To60AmountBase,
            report.Days61To90AmountBase,
            report.DaysOver90AmountBase,
            report.TotalOverdueAmountBase,
            report.TotalOutstandingAmountBase);

        builder.AppendBlankLine();
        builder.AppendRow("Section", "Vendor Summary");
        builder.AppendRow(
            "VendorEntityNumber",
            "VendorDisplayName",
            "Status",
            "OpenItemCount",
            "OldestDueDate",
            "CurrentAmountBase",
            "Days1To30AmountBase",
            "Days31To60AmountBase",
            "Days61To90AmountBase",
            "DaysOver90AmountBase",
            "TotalOverdueAmountBase",
            "TotalOutstandingAmountBase");

        foreach (var row in report.VendorRows)
        {
            builder.AppendRow(
                row.VendorEntityNumber,
                row.VendorDisplayName,
                row.VendorIsActive ? "Active" : "Inactive",
                row.OpenItemCount,
                row.OldestDueDate,
                row.CurrentAmountBase,
                row.Days1To30AmountBase,
                row.Days31To60AmountBase,
                row.Days61To90AmountBase,
                row.DaysOver90AmountBase,
                row.TotalOverdueAmountBase,
                row.TotalOutstandingAmountBase);
        }

        builder.AppendBlankLine();
        builder.AppendRow("Section", "Open Item Detail");
        builder.AppendRow(
            "VendorEntityNumber",
            "VendorDisplayName",
            "Status",
            "SourceType",
            "DisplayNumber",
            "DocumentDate",
            "DueDate",
            "DaysPastDue",
            "AgingBucket",
            "DocumentCurrencyCode",
            "BaseCurrencyCode",
            "BalanceSide",
            "OpenStatus",
            "OriginalAmountTx",
            "OriginalAmountBase",
            "OpenAmountTx",
            "OpenAmountBase",
            "SignedOpenAmountTx",
            "SignedOpenAmountBase");

        foreach (var row in report.DetailRows)
        {
            builder.AppendRow(
                row.VendorEntityNumber,
                row.VendorDisplayName,
                row.VendorIsActive ? "Active" : "Inactive",
                row.SourceType,
                row.DisplayNumber,
                row.DocumentDate,
                row.DueDate,
                row.DaysPastDue,
                row.AgingBucket,
                row.DocumentCurrencyCode,
                row.BaseCurrencyCode,
                row.BalanceSide,
                row.Status,
                row.OriginalAmountTx,
                row.OriginalAmountBase,
                row.OpenAmountTx,
                row.OpenAmountBase,
                row.SignedOpenAmountTx,
                row.SignedOpenAmountBase);
        }

        return new ReportCsvFile(
            $"ap-aging-{report.AsOfDate:yyyy-MM-dd}.csv",
            builder.ToString());
    }

    private static void AppendMetadata(CsvBuilder builder, string reportName, params (string Key, object? Value)[] rows)
    {
        builder.AppendRow("Report", reportName);

        foreach (var (key, value) in rows)
        {
            builder.AppendRow(key, value);
        }
    }

    private static void AppendIncomeSection(
        CsvBuilder builder,
        string sectionName,
        IReadOnlyList<IncomeStatementAccountSummary> rows,
        decimal sectionTotal)
    {
        builder.AppendBlankLine();
        builder.AppendRow("Section", sectionName);
        builder.AppendRow(
            "Code",
            "Name",
            "RootType",
            "DetailType",
            "EntityNumber",
            "Status",
            "System",
            "PostedDebitTotal",
            "PostedCreditTotal",
            "DisplayAmount");

        foreach (var row in rows)
        {
            builder.AppendRow(
                row.Code,
                row.Name,
                row.RootType,
                row.DetailType,
                row.EntityNumber,
                row.IsActive ? "Active" : "Inactive",
                row.IsSystem,
                row.PostedDebitTotal,
                row.PostedCreditTotal,
                row.DisplayAmount);
        }

        builder.AppendRow("SectionTotal", sectionName, sectionTotal);
    }

    private static void AppendBalanceSection(
        CsvBuilder builder,
        string sectionName,
        IReadOnlyList<BalanceSheetAccountSummary> rows,
        decimal sectionTotal)
    {
        builder.AppendBlankLine();
        builder.AppendRow("Section", sectionName);
        builder.AppendRow(
            "Code",
            "Name",
            "RootType",
            "DetailType",
            "EntityNumber",
            "Status",
            "System",
            "Synthetic",
            "PostedDebitTotal",
            "PostedCreditTotal",
            "DisplayAmount");

        foreach (var row in rows)
        {
            builder.AppendRow(
                row.Code,
                row.Name,
                row.RootType,
                row.DetailType,
                row.EntityNumber,
                row.IsActive ? "Active" : "Inactive",
                row.IsSystem,
                row.IsSynthetic,
                row.PostedDebitTotal,
                row.PostedCreditTotal,
                row.DisplayAmount);
        }

        builder.AppendRow("SectionTotal", sectionName, sectionTotal);
    }

    private static void AppendAgingMetadata(
        CsvBuilder builder,
        string reportName,
        Guid companyId,
        DateOnly asOfDate,
        string baseCurrencyCode,
        int counterpartyCount,
        int openItemCount,
        decimal currentAmountBase,
        decimal days1To30AmountBase,
        decimal days31To60AmountBase,
        decimal days61To90AmountBase,
        decimal daysOver90AmountBase,
        decimal totalOverdueAmountBase,
        decimal totalOutstandingAmountBase)
    {
        AppendMetadata(
            builder,
            reportName,
            ("CompanyId", companyId),
            ("AsOfDate", asOfDate),
            ("BaseCurrencyCode", baseCurrencyCode),
            ("CounterpartyCount", counterpartyCount),
            ("OpenItemCount", openItemCount),
            ("CurrentAmountBase", currentAmountBase),
            ("Days1To30AmountBase", days1To30AmountBase),
            ("Days31To60AmountBase", days31To60AmountBase),
            ("Days61To90AmountBase", days61To90AmountBase),
            ("DaysOver90AmountBase", daysOver90AmountBase),
            ("TotalOverdueAmountBase", totalOverdueAmountBase),
            ("TotalOutstandingAmountBase", totalOutstandingAmountBase));
    }

    public sealed record ReportCsvFile(string FileName, string Content, string ContentType = "text/csv; charset=utf-8");

    private sealed class CsvBuilder
    {
        private readonly StringBuilder _builder = new();

        public void AppendRow(params object?[] values)
        {
            var rendered = values.Select(FormatValue);
            _builder.AppendJoin(',', rendered);
            _builder.Append("\r\n");
        }

        public void AppendBlankLine() => _builder.Append("\r\n");

        public override string ToString() => _builder.ToString();

        private static string FormatValue(object? value)
        {
            if (value is null)
            {
                return string.Empty;
            }

            var rendered = value switch
            {
                string text => text,
                bool boolean => boolean ? "true" : "false",
                DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                decimal amount => amount.ToString("0.00", CultureInfo.InvariantCulture),
                Guid guid => guid.ToString("D"),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };

            return Escape(rendered);
        }

        private static string Escape(string value)
        {
            if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\r') && !value.Contains('\n'))
            {
                return value;
            }

            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }
    }
}
