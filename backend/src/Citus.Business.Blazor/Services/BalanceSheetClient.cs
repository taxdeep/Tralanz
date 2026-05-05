using System.Net;
using Citus.Ui.Shared.Reports;

namespace Citus.Business.Blazor.Services;

public sealed class BalanceSheetClient(HttpClient httpClient, ILogger<BalanceSheetClient> logger)
{
    public Task<ReportCsvDownload?> ExportCsvAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        bool includeZeroBalances,
        CancellationToken cancellationToken = default) =>
        ReportExportClientSupport.TryGetCsvAsync(
            httpClient,
            logger,
            $"accounting/reports/balance-sheet/export.csv?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}&includeZeroBalances={includeZeroBalances.ToString().ToLowerInvariant()}",
            "Balance Sheet CSV is unavailable because the active company is not provisioned in the accounting core yet.",
            "Unable to export the balance sheet report.",
            cancellationToken);

    public async Task<BalanceSheetReportSummary?> GetReportAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        bool includeZeroBalances,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/reports/balance-sheet?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}&includeZeroBalances={includeZeroBalances.ToString().ToLowerInvariant()}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Balance Sheet is unavailable because the active company is not provisioned in the accounting core yet.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<BalanceSheetReportSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load the balance sheet report.");
            return null;
        }
    }
}
