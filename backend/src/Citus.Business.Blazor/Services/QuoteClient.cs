using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company quote (estimate) catalog. Mirrors
/// <c>/accounting/quotes</c>. Save / Send / Accept / Convert all flow
/// through this client.
/// </summary>
public sealed class QuoteClient(HttpClient httpClient, ILogger<QuoteClient> logger)
{
    public async Task<IReadOnlyList<QuoteSummaryDto>> ListAsync(
        bool includeDrafts = true,
        string? status = null,
        Guid? customerId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>(3);
            if (!includeDrafts) query.Add("includeDrafts=false");
            if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
            if (customerId is { } cid) query.Add($"customerId={cid:D}");
            var url = query.Count == 0 ? "accounting/quotes" : $"accounting/quotes?{string.Join('&', query)}";

            var rows = await httpClient.GetFromJsonAsync<QuoteSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<QuoteSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read quotes.");
            return Array.Empty<QuoteSummaryDto>();
        }
    }

    public async Task<QuoteRecordDto?> GetByIdAsync(
        Guid quoteId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<QuoteRecordDto>(
                $"accounting/quotes/{quoteId:D}",
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read quote {QuoteId}.", quoteId);
            return null;
        }
    }

    public Task<QuoteMutationOutcome> CreateAsync(
        QuoteUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Post, "accounting/quotes", payload, cancellationToken);

    public Task<QuoteMutationOutcome> UpdateAsync(
        Guid id,
        QuoteUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Put, $"accounting/quotes/{id:D}", payload, cancellationToken);

    public async Task<QuoteMutationOutcome> SetStatusAsync(
        Guid id,
        string status,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/quotes/{id:D}/status",
                new { status },
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new QuoteMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<QuoteRecordDto>(cancellationToken);
            return new QuoteMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to set quote status.");
            return new QuoteMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    public async Task<SalesOrderConvertOutcome> ConvertToSalesOrderAsync(
        Guid quoteId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/quotes/{quoteId:D}/convert-to-sales-order",
                content: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SalesOrderConvertOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<SalesOrderRecordDto>(cancellationToken);
            return new SalesOrderConvertOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to convert quote to sales order.");
            return new SalesOrderConvertOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<QuoteMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        QuoteUpsertPayload payload,
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
                return new QuoteMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<QuoteRecordDto>(cancellationToken);
            return new QuoteMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert quote.");
            return new QuoteMutationOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record QuoteSummaryDto(
    Guid Id,
    Guid CompanyId,
    string QuoteNumber,
    Guid CustomerId,
    string CustomerName,
    DateOnly DocumentDate,
    DateOnly? ExpirationDate,
    string Status,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record QuoteRecordDto(
    Guid Id,
    Guid CompanyId,
    string QuoteNumber,
    string Status,
    Guid CustomerId,
    string CustomerName,
    DateOnly DocumentDate,
    DateOnly? ExpirationDate,
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
    Guid? ConvertedSalesOrderId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<QuoteLineDto> Lines);

public sealed record QuoteLineDto(
    Guid Id,
    Guid QuoteId,
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    string? AccountCode,
    decimal LineTotal);

public sealed record QuoteUpsertPayload(
    Guid CustomerId,
    DateOnly DocumentDate,
    DateOnly? ExpirationDate,
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
    IReadOnlyList<QuoteLinePayload> Lines);

public sealed record QuoteLinePayload(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    string? AccountCode);

public sealed record QuoteMutationOutcome(bool Succeeded, QuoteRecordDto? Saved, string? ErrorMessage);

public sealed record SalesOrderConvertOutcome(bool Succeeded, SalesOrderRecordDto? Saved, string? ErrorMessage);
