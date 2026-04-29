using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class AccountingDocumentReviewClient(HttpClient httpClient, ILogger<AccountingDocumentReviewClient> logger)
{
    /// <summary>
    /// Fetches the rendered invoice as a PDF byte stream. Caller is
    /// responsible for handing the bytes to the browser (e.g. via JS
    /// interop createObjectURL + click).
    /// </summary>
    public async Task<InvoicePdfDownload?> GetInvoicePdfAsync(
        Guid companyId,
        Guid invoiceId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/document-review/invoice/{invoiceId:D}/pdf?companyId={companyId:D}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation(
                    "Invoice PDF not available — invoice {InvoiceId} was not found in company {CompanyId}.",
                    invoiceId,
                    companyId);
                return null;
            }

            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? $"invoice-{invoiceId:N}.pdf";
            return new InvoicePdfDownload(bytes, fileName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to download invoice PDF for {InvoiceId}.", invoiceId);
            return null;
        }
    }

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
