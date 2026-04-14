using System.Net;

namespace Citus.Business.Blazor.Services;

internal static class ReportExportClientSupport
{
    public static async Task<ReportCsvDownload?> TryGetCsvAsync(
        HttpClient httpClient,
        ILogger logger,
        string requestUri,
        string unavailableMessage,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation(unavailableMessage);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var fileName =
                response.Content.Headers.ContentDisposition?.FileNameStar ??
                response.Content.Headers.ContentDisposition?.FileName ??
                "report.csv";

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            return new ReportCsvDownload
            {
                FileName = NormalizeFileName(fileName),
                Content = content.TrimStart('\uFEFF'),
                ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/csv; charset=utf-8"
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, failureMessage);
            return null;
        }
    }

    private static string NormalizeFileName(string value) => value.Trim().Trim('"');
}
