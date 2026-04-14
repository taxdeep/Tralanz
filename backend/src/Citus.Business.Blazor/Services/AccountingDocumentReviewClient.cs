using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class AccountingDocumentReviewClient(HttpClient httpClient, ILogger<AccountingDocumentReviewClient> logger)
{
    public async Task<AccountingDocumentReviewSummary?> GetDocumentAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        if (!AccountingDocumentReviewRouteCatalog.TryBuildApiPath(sourceType, documentId, out var requestPath))
        {
            logger.LogInformation("Unsupported accounting document review source type {SourceType}.", sourceType);
            return null;
        }

        var requestUri = $"{requestPath}?companyId={companyId:D}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation(
                    "Accounting document review is unavailable because {SourceType} {DocumentId} was not found in company {CompanyId}.",
                    sourceType,
                    documentId,
                    companyId);
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<AccountingDocumentReviewSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load accounting document review for {SourceType} {DocumentId}.", sourceType, documentId);
            return null;
        }
    }
}
