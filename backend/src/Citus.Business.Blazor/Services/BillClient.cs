using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the AP-side Bill (vendor invoice) document
/// surface. Mirrors <c>/accounting/ap/bills</c> on the Accounting
/// API. Save / Post / Void all flow through this client. The heavy
/// posting integration (FX snapshot, AP open item, journal entry)
/// gets wired in the next batch alongside PO + Inventory; V1 only
/// drives the document-level state machine.
/// </summary>
public sealed class BillClient(HttpClient httpClient, ILogger<BillClient> logger)
{
    public async Task<IReadOnlyList<BillSummaryDto>> ListAsync(
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
            var url = query.Count == 0 ? "accounting/ap/bills" : $"accounting/ap/bills?{string.Join('&', query)}";

            var rows = await httpClient.GetFromJsonAsync<BillSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<BillSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read bills.");
            return Array.Empty<BillSummaryDto>();
        }
    }

    public async Task<BillRecordDto?> GetByIdAsync(
        Guid billId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<BillRecordDto>(
                $"accounting/ap/bills/{billId:D}",
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read bill {BillId}.", billId);
            return null;
        }
    }

    public Task<BillMutationOutcome> CreateAsync(
        BillUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Post, "accounting/ap/bills", payload, cancellationToken);

    public Task<BillMutationOutcome> UpdateAsync(
        Guid id,
        BillUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Put, $"accounting/ap/bills/{id:D}", payload, cancellationToken);

    public Task<BillMutationOutcome> PostAsync(Guid id, CancellationToken cancellationToken = default)
        => SendStatusActionAsync($"accounting/ap/bills/{id:D}/post", cancellationToken);

    public Task<BillMutationOutcome> VoidAsync(Guid id, CancellationToken cancellationToken = default)
        => SendStatusActionAsync($"accounting/ap/bills/{id:D}/void", cancellationToken);

    private async Task<BillMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        BillUpsertPayload payload,
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
                return new BillMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<BillRecordDto>(cancellationToken);
            return new BillMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert bill.");
            return new BillMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<BillMutationOutcome> SendStatusActionAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsync(path, content: null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new BillMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<BillRecordDto>(cancellationToken);
            return new BillMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to transition bill status.");
            return new BillMutationOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record BillSummaryDto(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string BillNumber,
    Guid VendorId,
    string VendorName,
    DateOnly BillDate,
    DateOnly DueDate,
    string Status,
    string DocumentCurrencyCode,
    decimal TotalAmount,
    string? SourcePurchaseOrderNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BillRecordDto(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string BillNumber,
    string Status,
    Guid VendorId,
    string VendorName,
    DateOnly BillDate,
    DateOnly DueDate,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    decimal FxRate,
    string FxSource,
    decimal SubtotalAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    Guid? PaymentTermId,
    Guid? SourcePurchaseOrderId,
    string? SourcePurchaseOrderNumber,
    DateTimeOffset? PostedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<BillLineDto> Lines);

public sealed record BillLineDto(
    Guid Id,
    Guid BillId,
    int LineNumber,
    Guid ExpenseAccountId,
    string Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal TaxAmount);

public sealed record BillUpsertPayload(
    string BillNumber,
    Guid VendorId,
    DateOnly BillDate,
    DateOnly DueDate,
    string DocumentCurrencyCode,
    decimal? FxRate,
    string? Memo,
    Guid? PaymentTermId,
    Guid? SourcePurchaseOrderId,
    string? SourcePurchaseOrderNumber,
    IReadOnlyList<BillLinePayload> Lines);

public sealed record BillLinePayload(
    int LineNumber,
    Guid ExpenseAccountId,
    string Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal? TaxAmount);

public sealed record BillMutationOutcome(bool Succeeded, BillRecordDto? Saved, string? ErrorMessage);
