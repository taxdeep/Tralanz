using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// M7 iter 4: HTTP client for the year-end pre-close dashboard. One
/// read endpoint; failures degrade to an "all checks at zero"
/// placeholder so the page always renders something useful.
/// </summary>
public sealed class YearEndPreCloseClient(HttpClient httpClient, ILogger<YearEndPreCloseClient> logger)
{
    public async Task<YearEndPreCloseChecksDto?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<YearEndPreCloseChecksDto>(
                "accounting/year-end/pre-close-checks", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read year-end pre-close checks.");
            return null;
        }
    }
}

public sealed record YearEndPreCloseChecksDto(
    YearEndPreCloseCheckDto GrIrAged,
    YearEndPreCloseCheckDto DropShipClearingAged,
    YearEndPreCloseCheckDto SalesOrderBackorderAged);

public sealed record YearEndPreCloseCheckDto(
    string Title,
    string Description,
    int Count,
    string ResolutionHint);
