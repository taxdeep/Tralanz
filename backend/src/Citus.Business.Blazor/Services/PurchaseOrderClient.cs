using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the AP-side Purchase Order surface
/// (<c>/accounting/ap/purchase-orders</c>). Handles list / get /
/// create / edit / status / convert-to-bill.
/// </summary>
public sealed class PurchaseOrderClient(HttpClient httpClient, ILogger<PurchaseOrderClient> logger)
{
    public async Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListAsync(
        bool includeDrafts = true,
        string? status = null,
        Guid? vendorId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>(3);
            if (!includeDrafts) query.Add("includeDrafts=false");
            if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
            if (vendorId is { } vid) query.Add($"vendorId={vid:D}");
            var url = query.Count == 0 ? "accounting/ap/purchase-orders" : $"accounting/ap/purchase-orders?{string.Join('&', query)}";

            var rows = await httpClient.GetFromJsonAsync<PurchaseOrderSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<PurchaseOrderSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read purchase orders.");
            return Array.Empty<PurchaseOrderSummaryDto>();
        }
    }

    public async Task<PurchaseOrderRecordDto?> GetByIdAsync(
        Guid purchaseOrderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<PurchaseOrderRecordDto>(
                $"accounting/ap/purchase-orders/{purchaseOrderId:D}",
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read purchase order {PurchaseOrderId}.", purchaseOrderId);
            return null;
        }
    }

    public Task<PurchaseOrderMutationOutcome> CreateAsync(
        PurchaseOrderUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Post, "accounting/ap/purchase-orders", payload, cancellationToken);

    public Task<PurchaseOrderMutationOutcome> UpdateAsync(
        Guid id,
        PurchaseOrderUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Put, $"accounting/ap/purchase-orders/{id:D}", payload, cancellationToken);

    public async Task<PurchaseOrderMutationOutcome> SetStatusAsync(
        Guid id,
        string status,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/ap/purchase-orders/{id:D}/status",
                new { status },
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new PurchaseOrderMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<PurchaseOrderRecordDto>(cancellationToken);
            return new PurchaseOrderMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to set purchase order status.");
            return new PurchaseOrderMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    public async Task<BillConvertOutcome> ConvertToBillAsync(
        Guid purchaseOrderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/ap/purchase-orders/{purchaseOrderId:D}/convert-to-bill",
                content: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new BillConvertOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<BillRecordDto>(cancellationToken);
            return new BillConvertOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to convert purchase order to bill.");
            return new BillConvertOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<PurchaseOrderMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        PurchaseOrderUpsertPayload payload,
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
                // 409 + concurrency_conflict travels with the optimistic-
                // lock guard. Edit pages render that case with refresh-
                // and-retry CTA instead of the generic save-failed toast.
                var (message, code) = await ReadErrorAsync(response, cancellationToken);
                var isConflict =
                    response.StatusCode == System.Net.HttpStatusCode.Conflict
                    && string.Equals(code, "concurrency_conflict", StringComparison.OrdinalIgnoreCase);
                return new PurchaseOrderMutationOutcome(false, null, message, isConflict);
            }
            var saved = await response.Content.ReadFromJsonAsync<PurchaseOrderRecordDto>(cancellationToken);
            return new PurchaseOrderMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert purchase order.");
            return new PurchaseOrderMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var (message, _) = await ReadErrorAsync(response, cancellationToken);
        return message;
    }

    /// <summary>
    /// Pulls the (message, code) pair out of an error response in one
    /// pass — same shape as BillClient.ReadErrorAsync.
    /// </summary>
    private static async Task<(string Message, string? Code)> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ($"Request failed with status code {(int)response.StatusCode}.", null);
        }
        try
        {
            using var document = JsonDocument.Parse(raw);
            string? message = null;
            string? code = null;
            if (document.RootElement.TryGetProperty("message", out var messageEl) &&
                messageEl.ValueKind == JsonValueKind.String)
            {
                message = messageEl.GetString();
            }
            if (document.RootElement.TryGetProperty("code", out var codeEl) &&
                codeEl.ValueKind == JsonValueKind.String)
            {
                code = codeEl.GetString();
            }
            return (message ?? raw, code);
        }
        catch (JsonException) { }
        return (raw, null);
    }
}

public sealed record PurchaseOrderSummaryDto(
    Guid Id,
    CompanyId CompanyId,
    string PurchaseOrderNumber,
    Guid VendorId,
    string VendorName,
    DateOnly OrderDate,
    DateOnly? ExpectedDeliveryDate,
    string Status,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PurchaseOrderRecordDto(
    Guid Id,
    CompanyId CompanyId,
    string PurchaseOrderNumber,
    string Status,
    Guid VendorId,
    string VendorName,
    DateOnly OrderDate,
    DateOnly? ExpectedDeliveryDate,
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
    string? MemoToSupplier,
    string? InternalNote,
    Guid? PaymentTermId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PurchaseOrderLineDto> Lines);

public sealed record PurchaseOrderLineDto(
    Guid Id,
    Guid PurchaseOrderId,
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid? ExpenseAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    decimal LineTotal);

public sealed record PurchaseOrderUpsertPayload(
    Guid VendorId,
    DateOnly OrderDate,
    DateOnly? ExpectedDeliveryDate,
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
    string? MemoToSupplier,
    string? InternalNote,
    Guid? PaymentTermId,
    IReadOnlyList<PurchaseOrderLinePayload> Lines,
    // Optimistic-concurrency token. Edit pages capture the PO's
    // updated_at on load, then round-trip it on save. The API rejects
    // with HTTP 409 if the row's updated_at has moved since.
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record PurchaseOrderLinePayload(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid? ExpenseAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId);

public sealed record PurchaseOrderMutationOutcome(
    bool Succeeded,
    PurchaseOrderRecordDto? Saved,
    string? ErrorMessage,
    // True when the server returned 409 + code:"concurrency_conflict",
    // i.e. the PO was edited by another session between this editor's
    // GET and PUT. Pages render a refresh-and-retry CTA instead of a
    // generic "save failed" toast.
    bool IsConcurrencyConflict = false);

public sealed record BillConvertOutcome(bool Succeeded, BillRecordDto? Saved, string? ErrorMessage);
