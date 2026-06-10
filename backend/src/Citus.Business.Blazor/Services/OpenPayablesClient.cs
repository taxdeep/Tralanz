using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Wraps <c>GET /accounting/vendors/{vendorId}/open-payables</c> for the
/// Pay Bills page's Apply-to-Bills table — the AP mirror of
/// <see cref="OpenReceivablesClient"/>. Each row is an open A/P item (a bill
/// with a positive remaining balance) the operator can pay down. Failures
/// degrade to an empty list so the page stays usable while the network blips.
/// </summary>
public sealed class OpenPayablesClient(HttpClient httpClient, ILogger<OpenPayablesClient> logger)
{
    public async Task<IReadOnlyList<OpenPayableSummary>> ListAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken = default)
    {
        if (companyId.Value is null || vendorId == Guid.Empty)
        {
            return Array.Empty<OpenPayableSummary>();
        }

        var requestUri = $"accounting/vendors/{vendorId:D}/open-payables?companyId={companyId:D}";
        try
        {
            var rows = await httpClient.GetFromJsonAsync<OpenPayableSummary[]>(requestUri, cancellationToken);
            return rows ?? Array.Empty<OpenPayableSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load open payables for {VendorId} in {CompanyId}.", vendorId, companyId);
            return Array.Empty<OpenPayableSummary>();
        }
    }
}

/// <summary>
/// Wire shape returned by <c>/vendors/{id}/open-payables</c>. Mirrors the
/// projection the API hands back; the Pay Bills page reads display number /
/// due date / open balance for the Apply table.
/// </summary>
public sealed record OpenPayableSummary(
    Guid OpenItemId,
    string SourceType,
    Guid SourceDocumentId,
    string DisplayNumber,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    decimal OriginalAmountTx,
    decimal OpenAmountTx,
    decimal OpenAmountBase,
    string BalanceSide,
    string Status);
