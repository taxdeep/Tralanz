using System.Net;
using System.Net.Http.Json;

namespace Web.Shell.Services;

public sealed class ShellAccountingDocumentReviewClient(HttpClient httpClient, ILogger<ShellAccountingDocumentReviewClient> logger)
{
    public async Task<ShellAccountingDocumentReviewSummary?> GetDocumentAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildApiPath(sourceType, documentId, out var requestPath))
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
            return await response.Content.ReadFromJsonAsync<ShellAccountingDocumentReviewSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load accounting document review for {SourceType} {DocumentId}.", sourceType, documentId);
            return null;
        }
    }

    private static bool TryBuildApiPath(string? sourceType, Guid documentId, out string requestPath)
    {
        var normalized = Normalize(sourceType);
        requestPath = normalized is null
            ? string.Empty
            : $"accounting/document-review/{normalized}/{documentId:D}";

        return normalized is not null;
    }

    private static string? Normalize(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return null;
        }

        return sourceType.Trim().ToLowerInvariant() switch
        {
            "invoice" => "invoice",
            "credit_note" => "credit_note",
            "bill" => "bill",
            "vendor_credit" => "vendor_credit",
            "receive_payment" => "receive_payment",
            "credit_application" => "credit_application",
            "pay_bill" => "pay_bill",
            "vendor_credit_application" => "vendor_credit_application",
            _ => null
        };
    }
}
