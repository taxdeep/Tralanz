using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the Customer detail page's read aggregates:
///   * Financial summary (open balance, overdue count, unbilled work)
///   * Transactions timeline (invoices + sales orders + quotes)
///
/// Both surfaces fail soft — exceptions are swallowed, returns null /
/// empty so the page can render a "couldn't load" panel instead of
/// throwing the Blazor circuit. Active-company isolation is enforced
/// server-side via the X-Citus-Business-* headers attached by
/// <see cref="BusinessSessionHeaderHandler"/>; the caller never passes
/// a company id.
/// </summary>
public sealed class CustomerOverviewClient(HttpClient httpClient, ILogger<CustomerOverviewClient> logger)
{
    public async Task<CustomerFinancialSummaryDto?> GetFinancialSummaryAsync(
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<CustomerFinancialSummaryDto>(
                $"accounting/customers/{customerId:D}/financial-summary",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read customer financial summary for {CustomerId}.", customerId);
            return null;
        }
    }

    public async Task<IReadOnlyList<CustomerTransactionDto>> ListTransactionsAsync(
        Guid customerId,
        CustomerTransactionFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = BuildTransactionsUrl(customerId, filter);
            var rows = await httpClient.GetFromJsonAsync<CustomerTransactionDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<CustomerTransactionDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list customer transactions for {CustomerId}.", customerId);
            return Array.Empty<CustomerTransactionDto>();
        }
    }

    private static string BuildTransactionsUrl(Guid customerId, CustomerTransactionFilterDto filter)
    {
        var sb = new StringBuilder($"accounting/customers/{customerId:D}/transactions");
        var first = true;

        void Append(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            sb.Append(first ? '?' : '&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }

        Append("type", filter.Type);
        Append("status", filter.Status);
        if (filter.From.HasValue) Append("from", filter.From.Value.ToString("yyyy-MM-dd"));
        if (filter.To.HasValue) Append("to", filter.To.Value.ToString("yyyy-MM-dd"));

        return sb.ToString();
    }
}

public sealed record CustomerFinancialSummaryDto(
    decimal OpenBalanceBase,
    int OverdueInvoiceCount,
    decimal UnbilledWorkBase,
    string BaseCurrencyCode);

public sealed record CustomerTransactionDto(
    DateOnly Date,
    string Type,
    string DisplayNumber,
    Guid SourceId,
    string? Memo,
    decimal Amount,
    string CurrencyCode,
    string Status);

public sealed record CustomerTransactionFilterDto(
    string? Type = null,
    string? Status = null,
    DateOnly? From = null,
    DateOnly? To = null);
