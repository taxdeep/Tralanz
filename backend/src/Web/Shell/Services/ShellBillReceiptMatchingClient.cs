using System.Net.Http.Json;

namespace Web.Shell.Services;

public sealed class ShellBillReceiptMatchingClient(HttpClient httpClient, ILogger<ShellBillReceiptMatchingClient> logger)
{
    public async Task<WebShellAuthenticatedApiResult<ShellBillReceiptMatchingSummary>> GetBillSummaryAsync(
        Guid companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/bills/{billDocumentId:D}/receipt-matching?companyId={companyId:D}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellBillReceiptMatchingSummary>.RequiresAuthentication();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return WebShellAuthenticatedApiResult<ShellBillReceiptMatchingSummary>.NotFound(
                    "Bill receipt matching summary was not found in the active company context.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorMessageAsync(response, cancellationToken);
                logger.LogWarning(
                    "Unable to load bill receipt matching summary for bill {BillDocumentId} in company {CompanyId}: {Error}",
                    billDocumentId,
                    companyId,
                    error);
                return WebShellAuthenticatedApiResult<ShellBillReceiptMatchingSummary>.Failure(error);
            }

            var summary = await response.Content.ReadFromJsonAsync<ShellBillReceiptMatchingSummary>(cancellationToken);
            return WebShellAuthenticatedApiResult<ShellBillReceiptMatchingSummary>.Success(summary);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Unable to load bill receipt matching summary for bill {BillDocumentId} in company {CompanyId}.",
                billDocumentId,
                companyId);
            return WebShellAuthenticatedApiResult<ShellBillReceiptMatchingSummary>.Failure(
                "Unable to load receipt-first matching truth right now.");
        }
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ShellErrorPayload>(cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload?.Message))
            {
                return payload.Message;
            }
        }
        catch
        {
        }

        return $"Loading bill receipt matching failed with HTTP {(int)response.StatusCode}.";
    }

    private sealed record class ShellErrorPayload(string? Message);
}
