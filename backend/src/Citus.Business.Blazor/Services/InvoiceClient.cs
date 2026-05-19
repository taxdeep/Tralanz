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
    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListAsync(
        CompanyId companyId,
        bool includeDrafts = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/invoices?companyId={companyId:D}&includeDrafts={(includeDrafts ? "true" : "false")}";
            var rows = await httpClient.GetFromJsonAsync<InvoiceSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<InvoiceSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list invoices.");
            return Array.Empty<InvoiceSummaryDto>();
        }
    }

    public async Task<InvoiceRecordDto?> GetByIdAsync(
        Guid invoiceId,
        CompanyId companyId,
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

public sealed record InvoiceSummaryDto(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    Guid CustomerId,
    string CustomerName,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    DateTimeOffset? PostedAt,
    string? CustomerPoNumber,
    Guid? SalesOrderId);

public sealed record InvoiceRecordDto(
    Guid Id,
    CompanyId CompanyId,
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
    string? CustomerPoNumber,
    Guid? SalesOrderId,
    IReadOnlyList<InvoiceLineDto> Lines);

public sealed record InvoiceLineDto(
    int LineNumber,
    Guid? RevenueAccountId,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineAmount,
    decimal TaxAmount,
    Guid? PayableTaxAccountId,
    // Surfaced so the CreditMemoCreatePage "From invoice" pre-fill
    // can propagate per-line task attribution. New invoices created
    // without a Task source emit null here.
    Guid? TaskId = null);
