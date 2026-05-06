using System.Net;
using Citus.Ui.Shared.Reports;

namespace Citus.Business.Blazor.Services;

public sealed class ApAgingClient(HttpClient httpClient, ILogger<ApAgingClient> logger)
{
    public Task<ReportCsvDownload?> ExportCsvAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default) =>
        ReportExportClientSupport.TryGetCsvAsync(
            httpClient,
            logger,
            $"accounting/reports/ap-aging/export.csv?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}",
            "A/P aging CSV is unavailable because the active company is not provisioned in the accounting core yet.",
            "Unable to export the A/P aging report.",
            cancellationToken);

    public async Task<ApAgingReportSummary?> GetReportAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/reports/ap-aging?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("A/P aging is unavailable because the active company is not provisioned in the accounting core yet.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ApAgingReportSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load the A/P aging report.");
            return null;
        }
    }
}
