using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company Tax Code bundles (<c>tax_code_sets</c>) —
/// the R2 "Tax Code" layer that groups Tax Rules. Mirrors
/// <see cref="TaxCodeClient"/> against <c>/accounting/tax-code-sets</c>.
/// List-only for slice 1a (the per-line picker); create/edit arrives with
/// the Tax Code editor.
/// </summary>
public sealed class TaxCodeSetClient(HttpClient httpClient, ILogger<TaxCodeSetClient> logger)
{
    /// <summary>
    /// Fetches the Tax Codes for the active company. <paramref name="appliesTo"/>
    /// can be "sales", "purchase", "both", or null; "purchase" returns codes
    /// whose applies_to is purchase OR both (server-side filtered).
    /// </summary>
    public async Task<IReadOnlyList<TaxCodeSetSummary>> ListAsync(
        string? appliesTo = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(appliesTo))
            {
                query.Add($"appliesTo={Uri.EscapeDataString(appliesTo)}");
            }
            if (includeInactive)
            {
                query.Add("includeInactive=true");
            }
            var url = query.Count == 0
                ? "accounting/tax-code-sets"
                : $"accounting/tax-code-sets?{string.Join('&', query)}";

            var rows = await httpClient.GetFromJsonAsync<TaxCodeSetSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<TaxCodeSetSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read tax code sets (appliesTo={AppliesTo}).", appliesTo ?? "<all>");
            return Array.Empty<TaxCodeSetSummary>();
        }
    }
}

public sealed record TaxCodeSetSummary(
    Guid Id,
    string Code,
    string Name,
    string AppliesTo,
    bool IsActive,
    int RuleCount);
