using System.Net;
using Citus.Ui.Shared.Reports;

namespace Citus.Business.Blazor.Services;

public sealed class ArAgingClient(HttpClient httpClient, ILogger<ArAgingClient> logger)
{
    public Task<ReportCsvDownload?> ExportCsvAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default) =>
        ReportExportClientSupport.TryGetCsvAsync(
            httpClient,
            logger,
            $"accounting/reports/ar-aging/export.csv?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}",
            "A/R aging CSV is unavailable because the active company is not provisioned in the accounting core yet.",
            "Unable to export the A/R aging report.",
            cancellationToken);

    public async Task<ArAgingReportSummary?> GetReportAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/reports/ar-aging?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("A/R aging is unavailable because the active company is not provisioned in the accounting core yet.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ArAgingReportSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load the A/R aging report.");
            return null;
        }
    }
}
