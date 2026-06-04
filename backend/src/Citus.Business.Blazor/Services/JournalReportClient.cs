using System.Net;
using Citus.Ui.Shared.Reports;

namespace Citus.Business.Blazor.Services;

public sealed class JournalReportClient(HttpClient httpClient, ILogger<JournalReportClient> logger)
{
    public async Task<JournalReportSummary?> GetReportAsync(
        CompanyId companyId,
        DateOnly dateFrom,
        DateOnly dateTo,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/reports/journal?companyId={companyId:D}&dateFrom={dateFrom:yyyy-MM-dd}&dateTo={dateTo:yyyy-MM-dd}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Journal report is unavailable because the active company is not provisioned in the accounting core yet.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JournalReportSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load the journal report.");
            return null;
        }
    }
}
