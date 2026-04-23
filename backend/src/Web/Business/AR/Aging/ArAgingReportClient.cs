using System.Net.Http.Json;

namespace Web.Business.AR.Aging;

public sealed class ArAgingReportClient(HttpClient httpClient)
{
    public async Task<ArAgingReportResult> GetAsync(
        Guid companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/reports/ar-aging?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}";
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return new ArAgingReportResult
            {
                Value = await response.Content.ReadFromJsonAsync<ArAgingReportViewModel>(cancellationToken)
            };
        }

        var error = await response.Content.ReadFromJsonAsync<ErrorPayload>(cancellationToken);
        return new ArAgingReportResult
        {
            ErrorCode = error?.Code,
            ErrorMessage = error?.Message,
            IsNotFound = response.StatusCode == System.Net.HttpStatusCode.NotFound
        };
    }

    private sealed record class ErrorPayload(string? Code, string? Message);
}
