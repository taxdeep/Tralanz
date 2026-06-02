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

    /// <summary>
    /// Reverse a POSTED bill: posts a compensating journal entry (incl. every
    /// per-rule recoverable-tax leg) and flips the bill to 'reversed'. HTTP
    /// 200 genuinely means executed. Mirror of <c>InvoiceClient.ReverseAsync</c>.
    /// </summary>
    public async Task<BillReverseOutcome> ReverseAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/bills/{billId:D}/reverse";
        try
        {
            using var response = await httpClient.PostAsync(requestUri, content: null, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new BillReverseOutcome(true, null);
            }

            return new BillReverseOutcome(false, await ReadMessageAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to reverse bill {BillId}.", billId);
            return new BillReverseOutcome(false, "Could not reach the server to reverse the bill. Please retry.");
        }
    }

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
                // Read the body once and pull both message + code out of
                // it. The optimistic-lock guard (commit fc05168) returns
                // 409 + { code: "concurrency_conflict", message: ... };
                // pages render that case with a refresh-and-retry CTA
                // instead of the generic "save failed" toast.
                var (message, code) = await ReadErrorAsync(response, cancellationToken);
                var isConflict =
                    response.StatusCode == System.Net.HttpStatusCode.Conflict
                    && string.Equals(code, "concurrency_conflict", StringComparison.OrdinalIgnoreCase);
                return new BillMutationOutcome(false, null, message, isConflict);
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
        var (message, _) = await ReadErrorAsync(response, cancellationToken);
        return message;
    }

    /// <summary>
    /// Pulls the (message, code) pair out of an error response in one
    /// pass. Backend errors ship as { "message": "...", "code": "..." };
    /// non-JSON or message-only bodies still produce a useful string.
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

public sealed record BillSummaryDto(
    Guid Id,
    CompanyId CompanyId,
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
    CompanyId CompanyId,
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
    decimal TaxAmount,
    // Optional Task back-link surfaced on read so the bill edit page
    // can pre-fill the per-line TaskPicker from the persisted value.
    Guid? TaskId = null);

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
    IReadOnlyList<BillLinePayload> Lines,
    // Optimistic-concurrency token. Edit pages capture the bill's
    // updated_at when GET-loading, then round-trip it on save. The
    // API rejects with HTTP 409 if the row's updated_at has moved
    // since — see BillMutationOutcome.IsConcurrencyConflict.
    DateTimeOffset? ExpectedUpdatedAt = null,
    // Copy A3 Phase 2: when set, the form was prefilled from an
    // existing bill via More → Copy. Server writes a bill_copied
    // audit_logs row.
    Guid? CopiedFromBillId = null);

public sealed record BillLinePayload(
    int LineNumber,
    Guid ExpenseAccountId,
    string Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal? TaxAmount,
    // Optional Task back-link sent on write. Server validates via
    // ITaskLineLinkValidator (Batch 8) before insert and persists the
    // value to bill_lines.task_id.
    Guid? TaskId = null);

public sealed record BillMutationOutcome(
    bool Succeeded,
    BillRecordDto? Saved,
    string? ErrorMessage,
    // True when the server returned 409 + code:"concurrency_conflict",
    // i.e. the bill was edited by another session between this editor's
    // GET and PUT. Pages use this to render a refresh-and-retry CTA
    // instead of a generic "save failed" toast.
    bool IsConcurrencyConflict = false);

public sealed record BillReverseOutcome(bool Succeeded, string? ErrorMessage);
