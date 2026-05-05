using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company sales-order catalog. Mirrors
/// <c>/accounting/sales-orders</c>.
/// </summary>
public sealed class SalesOrderClient(HttpClient httpClient, ILogger<SalesOrderClient> logger)
{
    public async Task<IReadOnlyList<SalesOrderSummaryDto>> ListAsync(
        string? status = null,
        Guid? customerId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
            if (customerId is { } cid) query.Add($"customerId={cid:D}");
            var url = query.Count == 0 ? "accounting/sales-orders" : $"accounting/sales-orders?{string.Join('&', query)}";

            var rows = await httpClient.GetFromJsonAsync<SalesOrderSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<SalesOrderSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read sales orders.");
            return Array.Empty<SalesOrderSummaryDto>();
        }
    }

    public async Task<SalesOrderRecordDto?> GetByIdAsync(
        Guid salesOrderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<SalesOrderRecordDto>(
                $"accounting/sales-orders/{salesOrderId:D}",
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read sales order {SalesOrderId}.", salesOrderId);
            return null;
        }
    }

    public Task<SalesOrderMutationOutcome> CreateAsync(
        SalesOrderUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Post, "accounting/sales-orders", payload, cancellationToken);

    public Task<SalesOrderMutationOutcome> UpdateAsync(
        Guid id,
        SalesOrderUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Put, $"accounting/sales-orders/{id:D}", payload, cancellationToken);

    public async Task<SalesOrderMutationOutcome> SetStatusAsync(
        Guid id,
        string status,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/sales-orders/{id:D}/status",
                new { status },
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SalesOrderMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<SalesOrderRecordDto>(cancellationToken);
            return new SalesOrderMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to set sales order status.");
            return new SalesOrderMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    public async Task<SalesOrderMutationOutcome> MarkInvoicedAsync(
        Guid id,
        string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/sales-orders/{id:D}/mark-invoiced",
                new { invoiceNumber },
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SalesOrderMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<SalesOrderRecordDto>(cancellationToken);
            return new SalesOrderMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to mark sales order invoiced.");
            return new SalesOrderMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    /// <summary>
    /// M5 iter 1: confirms an Open SO. Server splits each Stock-line's
    /// quantity into reserved / backorder, bumps the warehouse balance,
    /// and flips status to 'confirmed'. Backorder-disallowed items fail
    /// with a precise shortage message returned in
    /// <see cref="SalesOrderMutationOutcome.ErrorMessage"/>.
    /// </summary>
    public async Task<SalesOrderMutationOutcome> ConfirmAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/sales-orders/{id:D}/confirm",
                content: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SalesOrderMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<SalesOrderRecordDto>(cancellationToken);
            return new SalesOrderMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to confirm sales order.");
            return new SalesOrderMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    /// <summary>
    /// M5 iter 5: cancels an Open or Confirmed SO. Server releases any
    /// reservations and surfaces an open-deposit count + total in the
    /// outcome so the UI can warn the operator about leftover liability.
    /// </summary>
    public async Task<SalesOrderCancelOutcome> CancelAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/sales-orders/{id:D}/cancel",
                content: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SalesOrderCancelOutcome(false, null, 0, 0m, await ReadMessageAsync(response, cancellationToken));
            }
            var body = await response.Content.ReadFromJsonAsync<SalesOrderCancelResponseDto>(cancellationToken);
            return new SalesOrderCancelOutcome(
                Succeeded: true,
                Saved: body?.SalesOrder,
                OpenDepositCount: body?.OpenDepositCount ?? 0,
                OpenDepositTotalBase: body?.OpenDepositTotalBase ?? 0m,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to cancel sales order.");
            return new SalesOrderCancelOutcome(false, null, 0, 0m, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<SalesOrderMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        SalesOrderUpsertPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(payload),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SalesOrderMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<SalesOrderRecordDto>(cancellationToken);
            return new SalesOrderMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert sales order.");
            return new SalesOrderMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return $"Request failed with status code {(int)response.StatusCode}.";
        }
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? raw;
            }
        }
        catch (JsonException) { }
        return raw;
    }
}

public sealed record SalesOrderSummaryDto(
    Guid Id,
    CompanyId CompanyId,
    string SalesOrderNumber,
    Guid CustomerId,
    string CustomerName,
    DateOnly DocumentDate,
    string Status,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    Guid? SourceQuoteId,
    string? InvoiceNumber,
    string? CustomerPoNumber,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SalesOrderRecordDto(
    Guid Id,
    CompanyId CompanyId,
    string SalesOrderNumber,
    string Status,
    Guid CustomerId,
    string CustomerName,
    DateOnly DocumentDate,
    string TransactionCurrencyCode,
    decimal? FxRate,
    string? BillingAddressLine,
    string? BillingCity,
    string? BillingProvinceState,
    string? BillingPostalCode,
    string? BillingCountry,
    string? ShippingAddressLine,
    string? ShippingCity,
    string? ShippingProvinceState,
    string? ShippingPostalCode,
    string? ShippingCountry,
    string? ShipVia,
    DateOnly? ShippingDate,
    string? TrackingNo,
    string TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    decimal? ShippingAmount,
    Guid? ShippingTaxCodeId,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? MemoToCustomer,
    string? InternalNote,
    Guid? SourceQuoteId,
    string? SourceQuoteNumber,
    string? InvoiceNumber,
    string? CustomerPoNumber,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SalesOrderLineDto> Lines);

public sealed record SalesOrderLineDto(
    Guid Id,
    Guid SalesOrderId,
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    string? AccountCode,
    decimal LineTotal,
    decimal ReservedQty = 0m,
    decimal BackorderQty = 0m,
    decimal ShippedQty = 0m);

public sealed record SalesOrderUpsertPayload(
    Guid CustomerId,
    DateOnly DocumentDate,
    string TransactionCurrencyCode,
    decimal? FxRate,
    string? BillingAddressLine,
    string? BillingCity,
    string? BillingProvinceState,
    string? BillingPostalCode,
    string? BillingCountry,
    string? ShippingAddressLine,
    string? ShippingCity,
    string? ShippingProvinceState,
    string? ShippingPostalCode,
    string? ShippingCountry,
    string? ShipVia,
    DateOnly? ShippingDate,
    string? TrackingNo,
    string TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    decimal? ShippingAmount,
    Guid? ShippingTaxCodeId,
    string? MemoToCustomer,
    string? InternalNote,
    Guid? SourceQuoteId,
    string? CustomerPoNumber,
    IReadOnlyList<SalesOrderLinePayload> Lines);

public sealed record SalesOrderLinePayload(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    string? AccountCode);

public sealed record SalesOrderMutationOutcome(bool Succeeded, SalesOrderRecordDto? Saved, string? ErrorMessage);

public sealed record SalesOrderCancelOutcome(
    bool Succeeded,
    SalesOrderRecordDto? Saved,
    int OpenDepositCount,
    decimal OpenDepositTotalBase,
    string? ErrorMessage);

internal sealed record SalesOrderCancelResponseDto(
    SalesOrderRecordDto SalesOrder,
    int OpenDepositCount,
    decimal OpenDepositTotalBase);
