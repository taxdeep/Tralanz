using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the AP-side Expense (cash outflow) surface
/// (<c>/accounting/ap/expenses</c>). Handles list / get / create
/// (which lands as Posted) / void.
/// </summary>
public sealed class ExpenseClient(HttpClient httpClient, ILogger<ExpenseClient> logger)
{
    public async Task<IReadOnlyList<ExpenseSummaryDto>> ListAsync(
        string? status = null,
        Guid? payeeId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
            if (payeeId is { } pid) query.Add($"payeeId={pid:D}");
            var url = query.Count == 0 ? "accounting/ap/expenses" : $"accounting/ap/expenses?{string.Join('&', query)}";

            var rows = await httpClient.GetFromJsonAsync<ExpenseSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<ExpenseSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read expenses.");
            return Array.Empty<ExpenseSummaryDto>();
        }
    }

    public async Task<ExpenseRecordDto?> GetByIdAsync(
        Guid expenseId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<ExpenseRecordDto>(
                $"accounting/ap/expenses/{expenseId:D}",
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read expense {ExpenseId}.", expenseId);
            return null;
        }
    }

    public async Task<ExpenseMutationOutcome> CreateAsync(
        ExpenseUpsertPayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "accounting/ap/expenses",
                payload,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ExpenseMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<ExpenseRecordDto>(cancellationToken);
            return new ExpenseMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to create expense.");
            return new ExpenseMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    public async Task<ExpenseMutationOutcome> ConvertFromPurchaseOrderAsync(
        Guid purchaseOrderId,
        ExpenseUpsertPayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/ap/purchase-orders/{purchaseOrderId:D}/convert-to-expense",
                payload,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ExpenseMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<ExpenseRecordDto>(cancellationToken);
            return new ExpenseMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to convert PO to expense.");
            return new ExpenseMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    public async Task<ExpenseMutationOutcome> VoidAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/ap/expenses/{id:D}/void",
                content: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ExpenseMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<ExpenseRecordDto>(cancellationToken);
            return new ExpenseMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to void expense.");
            return new ExpenseMutationOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record ExpenseSummaryDto(
    Guid Id,
    CompanyId CompanyId,
    string ExpenseNumber,
    string Status,
    string PayeeKind,
    Guid? PayeeId,
    string PayeeDisplayName,
    Guid PaymentAccountId,
    string PaymentAccountLabel,
    string PaymentMethod,
    DateOnly PaymentDate,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    string? SourcePurchaseOrderNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ExpenseRecordDto(
    Guid Id,
    CompanyId CompanyId,
    string ExpenseNumber,
    string Status,
    string PayeeKind,
    Guid? PayeeId,
    string PayeeNameFreeform,
    Guid PaymentAccountId,
    string PaymentAccountLabel,
    string PaymentMethod,
    string? ChequeNumber,
    string? RefNo,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal FxRate,
    string FxSource,
    DateOnly PaymentDate,
    Guid? SourcePurchaseOrderId,
    string? SourcePurchaseOrderNumber,
    string TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    string? InternalNote,
    Guid? PostedJournalEntryId,
    DateTimeOffset? VoidedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ExpenseLineDto> Lines);

public sealed record ExpenseLineDto(
    Guid Id,
    Guid ExpenseId,
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid ExpenseAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    decimal LineTotal,
    // Optional Task back-link surfaced on read so the expense detail
    // view (and any future edit form) can show the attribution.
    Guid? TaskId = null);

public sealed record ExpenseUpsertPayload(
    string PayeeKind,
    Guid? PayeeId,
    string? PayeeNameFreeform,
    Guid PaymentAccountId,
    string PaymentMethod,
    string? ChequeNumber,
    string? RefNo,
    string TransactionCurrencyCode,
    decimal? FxRate,
    DateOnly PaymentDate,
    Guid? SourcePurchaseOrderId,
    string? SourcePurchaseOrderNumber,
    string? TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    string? Memo,
    string? InternalNote,
    IReadOnlyList<ExpenseLinePayload> Lines,
    // Copy A3 Phase 1: when the form was prefilled from an existing
    // expense (via ExpenseDetailPage → More → Copy → /expenses/new?copyFrom=…),
    // the create page sets this so the server can audit the provenance.
    // Nullable + defaulted-null so non-copy flows don't have to know
    // about it.
    Guid? CopiedFromExpenseId = null);

public sealed record ExpenseLinePayload(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid ExpenseAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    // Optional Task back-link sent on write. Server validates via
    // ITaskLineLinkValidator and persists to expense_lines.task_id.
    Guid? TaskId = null,
    // R2: tax_code_sets.id — a Tax Code bundle selected on the line.
    Guid? TaxCodeSetId = null);

public sealed record ExpenseMutationOutcome(bool Succeeded, ExpenseRecordDto? Saved, string? ErrorMessage);
