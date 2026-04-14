using System.Net;
using System.Text.Json;
using Citus.Ui.Shared.Reports;

namespace Citus.Business.Blazor.Services;

public sealed class TrialBalanceClient(HttpClient httpClient, ILogger<TrialBalanceClient> logger)
{
    public Task<ReportCsvDownload?> ExportCsvAsync(
        Guid companyId,
        DateOnly asOfDate,
        bool includeZeroBalances,
        CancellationToken cancellationToken = default) =>
        ReportExportClientSupport.TryGetCsvAsync(
            httpClient,
            logger,
            $"accounting/reports/trial-balance/export.csv?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}&includeZeroBalances={includeZeroBalances.ToString().ToLowerInvariant()}",
            "Trial balance CSV is unavailable because the active company is not provisioned in the accounting core yet.",
            "Unable to export the trial balance report.",
            cancellationToken);

    public async Task<TrialBalanceReportSummary?> GetReportAsync(
        Guid companyId,
        DateOnly asOfDate,
        bool includeZeroBalances,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/reports/trial-balance?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}&includeZeroBalances={includeZeroBalances.ToString().ToLowerInvariant()}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Trial balance is unavailable because the active company is not provisioned in the accounting core yet.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TrialBalanceReportSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load the trial balance report.");
            return null;
        }
    }
}
