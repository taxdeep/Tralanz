using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Wraps <c>GET /accounting/customers/{customerId}/open-receivables</c> for the
/// Receive Payment page's Apply-to-Invoices table. Each row is a candidate
/// open item the operator can tick — invoices with a positive remaining
/// balance, plus customer deposits surfaced as negative rows once Commit B
/// brings them in. Failures degrade to an empty list so the page stays
/// usable while the network blips.
/// </summary>
public sealed class OpenReceivablesClient(HttpClient httpClient, ILogger<OpenReceivablesClient> logger)
{
    public async Task<IReadOnlyList<OpenReceivableSummary>> ListAsync(
        Guid companyId,
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty || customerId == Guid.Empty)
        {
            return Array.Empty<OpenReceivableSummary>();
        }

        var requestUri = $"accounting/customers/{customerId:D}/open-receivables?companyId={companyId:D}";
        try
        {
            var rows = await httpClient.GetFromJsonAsync<OpenReceivableSummary[]>(requestUri, cancellationToken);
            return rows ?? Array.Empty<OpenReceivableSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load open receivables for {CustomerId} in {CompanyId}.", customerId, companyId);
            return Array.Empty<OpenReceivableSummary>();
        }
    }
}

/// <summary>
/// Wire shape returned by <c>/customers/{id}/open-receivables</c>. Mirrors
/// the projection the API hands back; the receive-payment page reads
/// every field for the Apply table (display number / dates / amounts /
/// balance side) so they stay grouped here instead of an ad-hoc DTO.
/// </summary>
public sealed record OpenReceivableSummary(
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
