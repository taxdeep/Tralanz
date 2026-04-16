using System.Net.Http.Json;

namespace Web.Shell.Services;

public sealed class ShellAccountingDocumentBrowserClient(HttpClient httpClient, ILogger<ShellAccountingDocumentBrowserClient> logger)
{
    public async Task<WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>> GetDocumentsAsync(
        Guid companyId,
        string? sourceType,
        string? counterpartyRole,
        Guid? counterpartyId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/documents/source?companyId={companyId:D}&limit={Math.Clamp(limit, 1, 200)}";
        if (!string.IsNullOrWhiteSpace(sourceType))
        {
            requestUri += $"&sourceType={Uri.EscapeDataString(sourceType.Trim().ToLowerInvariant())}";
        }
        if (!string.IsNullOrWhiteSpace(counterpartyRole))
        {
            requestUri += $"&counterpartyRole={Uri.EscapeDataString(counterpartyRole.Trim().ToLowerInvariant())}";
        }
        if (counterpartyId.HasValue)
        {
            requestUri += $"&counterpartyId={counterpartyId.Value:D}";
        }

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.RequiresAuthentication();
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorMessageAsync(response, cancellationToken);
                logger.LogWarning("Unable to load accounting source documents for company {CompanyId}: {Error}", companyId, error);
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Failure(error);
            }

            var items = await response.Content.ReadFromJsonAsync<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>(cancellationToken)
                ?? Array.Empty<ShellAccountingSourceDocumentBrowserItem>();
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Success(items);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load accounting source documents for company {CompanyId}.", companyId);
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Failure("Unable to load accounting source documents right now.");
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

        return $"Loading documents failed with HTTP {(int)response.StatusCode}.";
    }

    private sealed record class ShellErrorPayload(string? Message);
}
