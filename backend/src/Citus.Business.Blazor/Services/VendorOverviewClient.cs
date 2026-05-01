using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the Vendor detail page's read aggregates — AP-side
/// mirror of <see cref="CustomerOverviewClient"/>:
///   * Financial summary (open balance, overdue bill count, open POs)
///   * Transactions timeline (bills + POs + vendor credits)
///
/// Both surfaces fail soft. Active-company isolation is enforced
/// server-side via the X-Citus-Business-* headers.
/// </summary>
public sealed class VendorOverviewClient(HttpClient httpClient, ILogger<VendorOverviewClient> logger)
{
    public async Task<VendorFinancialSummaryDto?> GetFinancialSummaryAsync(
        Guid vendorId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<VendorFinancialSummaryDto>(
                $"accounting/vendors/{vendorId:D}/financial-summary",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read vendor financial summary for {VendorId}.", vendorId);
            return null;
        }
    }

    public async Task<IReadOnlyList<VendorTransactionDto>> ListTransactionsAsync(
        Guid vendorId,
        VendorTransactionFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = BuildTransactionsUrl(vendorId, filter);
            var rows = await httpClient.GetFromJsonAsync<VendorTransactionDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<VendorTransactionDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list vendor transactions for {VendorId}.", vendorId);
            return Array.Empty<VendorTransactionDto>();
        }
    }

    private static string BuildTransactionsUrl(Guid vendorId, VendorTransactionFilterDto filter)
    {
        var sb = new StringBuilder($"accounting/vendors/{vendorId:D}/transactions");
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

public sealed record VendorFinancialSummaryDto(
    decimal OpenBalanceBase,
    int OverdueBillCount,
    int OpenPurchaseOrderCount,
    string BaseCurrencyCode);

public sealed record VendorTransactionDto(
    DateOnly Date,
    string Type,
    string DisplayNumber,
    Guid SourceId,
    string? Memo,
    decimal Amount,
    string CurrencyCode,
    string Status);

public sealed record VendorTransactionFilterDto(
    string? Type = null,
    string? Status = null,
    DateOnly? From = null,
    DateOnly? To = null);
