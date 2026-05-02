using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Thin read-side wrapper for the invoice document HTTP surface. The
/// write side runs through <see cref="BusinessWriteFlowClient"/> like
/// every other create flow; this client only exists so that read-only
/// pages (<c>InvoiceDetailPage</c>, future overview cards) can fetch a
/// single posted invoice by id without each page reinventing the
/// HTTP-binding ceremony.
/// </summary>
public sealed class InvoiceClient(HttpClient httpClient, ILogger<InvoiceClient> logger)
{
    public async Task<InvoiceRecordDto?> GetByIdAsync(
        Guid invoiceId,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/invoices/{invoiceId:D}?CompanyId={companyId:D}";
        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<InvoiceRecordDto>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load invoice {InvoiceId}.", invoiceId);
            return null;
        }
    }
}

public sealed record InvoiceRecordDto(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    Guid CustomerId,
    Guid? ReceivableAccountId,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal SubtotalAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    IReadOnlyList<InvoiceLineDto> Lines);

public sealed record InvoiceLineDto(
    int LineNumber,
    Guid? RevenueAccountId,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineAmount,
    decimal TaxAmount,
    Guid? PayableTaxAccountId);
