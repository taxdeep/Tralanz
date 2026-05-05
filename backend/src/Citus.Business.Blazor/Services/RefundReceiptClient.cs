using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class RefundReceiptClient(HttpClient httpClient, ILogger<RefundReceiptClient> logger)
{
    public async Task<IReadOnlyList<RefundReceiptSummaryDto>> ListAsync(
        CompanyId companyId,
        bool includeDrafts = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/refund-receipts?companyId={companyId:D}&includeDrafts={(includeDrafts ? "true" : "false")}";
            var rows = await httpClient.GetFromJsonAsync<RefundReceiptSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<RefundReceiptSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list refund receipts.");
            return Array.Empty<RefundReceiptSummaryDto>();
        }
    }

    public async Task<RefundReceiptRecordDto?> GetByIdAsync(
        Guid documentId,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"accounting/refund-receipts/{documentId:D}?CompanyId={companyId:D}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<RefundReceiptRecordDto>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load refund receipt {DocumentId}.", documentId);
            return null;
        }
    }
}

public sealed record RefundReceiptRecordDto(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly RefundDate,
    Guid CustomerId,
    Guid RefundFromAccountId,
    string PaymentMethod,
    string? ReferenceNo,
    string? Reason,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal? FxRate,
    decimal SubtotalAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    string? CustomerPoNumber,
    IReadOnlyList<RefundReceiptLineDto> Lines);

public sealed record RefundReceiptSummaryDto(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly RefundDate,
    Guid CustomerId,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    string PaymentMethod,
    DateTimeOffset? PostedAt,
    string? CustomerPoNumber);

public sealed record RefundReceiptLineDto(
    int LineNumber,
    Guid RevenueAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineAmount,
    decimal TaxAmount,
    Guid? TaxCodeId,
    Guid? PayableTaxAccountId);
