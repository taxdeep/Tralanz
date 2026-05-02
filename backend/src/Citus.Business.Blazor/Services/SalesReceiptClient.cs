using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Read-side wrapper for posted sales receipts. Mirror of
/// <see cref="InvoiceClient"/> — the write side runs through
/// <see cref="BusinessWriteFlowClient.PostSalesReceiptAsync"/>;
/// this client only reads.
/// </summary>
public sealed class SalesReceiptClient(HttpClient httpClient, ILogger<SalesReceiptClient> logger)
{
    public async Task<SalesReceiptRecordDto?> GetByIdAsync(
        Guid documentId,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"accounting/sales-receipts/{documentId:D}?CompanyId={companyId:D}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SalesReceiptRecordDto>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load sales receipt {DocumentId}.", documentId);
            return null;
        }
    }
}

public sealed record SalesReceiptRecordDto(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly ReceiptDate,
    Guid CustomerId,
    Guid DepositToAccountId,
    string PaymentMethod,
    string? ReferenceNo,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal? FxRate,
    decimal SubtotalAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    IReadOnlyList<SalesReceiptLineDto> Lines);

public sealed record SalesReceiptLineDto(
    int LineNumber,
    Guid RevenueAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineAmount,
    decimal TaxAmount,
    Guid? TaxCodeId,
    Guid? PayableTaxAccountId);
