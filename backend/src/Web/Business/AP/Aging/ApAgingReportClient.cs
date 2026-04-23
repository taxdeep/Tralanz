using System.Net.Http.Json;

namespace Web.Business.AP.Aging;

public sealed class ApAgingReportClient(HttpClient httpClient)
{
    public async Task<ApAgingReportResult> GetAsync(
        Guid companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/reports/ap-aging?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}";
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return new ApAgingReportResult
            {
                Value = await response.Content.ReadFromJsonAsync<ApAgingReportViewModel>(cancellationToken)
            };
        }

        var error = await response.Content.ReadFromJsonAsync<ErrorPayload>(cancellationToken);
        return new ApAgingReportResult
        {
            ErrorCode = error?.Code,
            ErrorMessage = error?.Message,
            IsNotFound = response.StatusCode == System.Net.HttpStatusCode.NotFound
        };
    }

    private sealed record class ErrorPayload(string? Code, string? Message);
}
