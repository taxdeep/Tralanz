using System.Net.Http.Json;
using Citus.Ui.Shared.Control;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Read-only HTTP client for the per-company module-flag list. Calls
/// <c>/accounting/company/module-flags</c> on the Accounting API,
/// which already enforces session + company isolation. Failures are
/// logged and the client returns an empty dictionary — the nav menu
/// treats "unknown" the same as "disabled", so the worst case is a
/// hidden module rather than a leak.
/// </summary>
public sealed class ModuleFlagsClient(HttpClient httpClient, ILogger<ModuleFlagsClient> logger)
{
    public async Task<IReadOnlyDictionary<string, bool>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await httpClient.GetFromJsonAsync<CompanyModuleFlagSummary[]>(
                "accounting/company/module-flags",
                cancellationToken);
            if (rows is null || rows.Length == 0)
            {
                return EmptyFlags;
            }

            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.ModuleKey))
                {
                    continue;
                }
                result[row.ModuleKey.Trim().ToLowerInvariant()] = row.Enabled;
            }
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read company module flags; nav will fall back to defaults.");
            return EmptyFlags;
        }
    }

    private static readonly IReadOnlyDictionary<string, bool> EmptyFlags =
        new Dictionary<string, bool>(StringComparer.Ordinal);
}
