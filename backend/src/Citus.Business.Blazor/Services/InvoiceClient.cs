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

    /// <summary>
    /// Reads the next auto invoice number (INV-######) WITHOUT reserving it,
    /// so the create page can pre-fill the editable "Invoice #" default.
    /// Returns null on any failure — the field then just starts blank.
    /// </summary>
    public async Task<string?> PeekNextNumberAsync(CompanyId companyId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"accounting/invoices/next-number?companyId={companyId:D}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = System.Text.Json.JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("nextNumber", out var el)
                   && el.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to peek the next invoice number.");
            return null;
        }
    }

    /// <summary>
    /// Reverses a posted invoice: the dedicated endpoint posts a
    /// compensating journal entry (reversing every leg, including each
    /// per-rule sales-tax leg) and flips the invoice to 'reversed' so it
    /// leaves the receivable set. Company / actor come from the session,
    /// so there is no request body and the call only succeeds (200) when
    /// the reversal actually executed.
    /// </summary>
    public async Task<InvoiceReverseOutcome> ReverseAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/invoices/{invoiceId:D}/reverse";
        try
        {
            using var response = await httpClient.PostAsync(requestUri, content: null, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new InvoiceReverseOutcome(true, null);
            }

            string? message = null;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("message", out var m) &&
                    m.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    message = m.GetString();
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Non-JSON error body — fall back to the status code message.
            }

            return new InvoiceReverseOutcome(false, message ?? $"Reverse failed (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to reverse invoice {InvoiceId}.", invoiceId);
            return new InvoiceReverseOutcome(false, "Could not reach the server to reverse the invoice. Please retry.");
        }
    }
}

public sealed record InvoiceReverseOutcome(bool Succeeded, string? ErrorMessage);

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
    string? BillingAddress,
    string? ShippingAddress,
    IReadOnlyList<InvoiceLineDto> Lines,
    IReadOnlyList<InvoiceTaxBreakdownLine>? TaxBreakdown = null);

public sealed record InvoiceTaxBreakdownLine(string Code, decimal Amount);

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
