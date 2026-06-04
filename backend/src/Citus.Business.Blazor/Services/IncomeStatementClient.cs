using System.Net;
using Citus.Ui.Shared.Reports;

namespace Citus.Business.Blazor.Services;

public sealed class IncomeStatementClient(HttpClient httpClient, ILogger<IncomeStatementClient> logger)
{
    public Task<ReportCsvDownload?> ExportCsvAsync(
        CompanyId companyId,
        DateOnly dateFrom,
        DateOnly dateTo,
        bool includeZeroBalances,
        string basis = "accrual",
        CancellationToken cancellationToken = default) =>
        ReportExportClientSupport.TryGetCsvAsync(
            httpClient,
            logger,
            $"accounting/reports/income-statement/export.csv?companyId={companyId:D}&dateFrom={dateFrom:yyyy-MM-dd}&dateTo={dateTo:yyyy-MM-dd}&includeZeroBalances={includeZeroBalances.ToString().ToLowerInvariant()}&basis={basis}",
            "Income Statement CSV is unavailable because the active company is not provisioned in the accounting core yet.",
            "Unable to export the income statement report.",
            cancellationToken);

    public async Task<IncomeStatementReportSummary?> GetReportAsync(
        CompanyId companyId,
        DateOnly dateFrom,
        DateOnly dateTo,
        bool includeZeroBalances,
        string basis = "accrual",
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/reports/income-statement?companyId={companyId:D}&dateFrom={dateFrom:yyyy-MM-dd}&dateTo={dateTo:yyyy-MM-dd}&includeZeroBalances={includeZeroBalances.ToString().ToLowerInvariant()}&basis={basis}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Income Statement is unavailable because the active company is not provisioned in the accounting core yet.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<IncomeStatementReportSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load the income statement report.");
            return null;
        }
    }
}
