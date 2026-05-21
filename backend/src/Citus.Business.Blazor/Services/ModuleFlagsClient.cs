using System.Net.Http.Json;
using Citus.Ui.Shared.Control;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company module-flag surface. Calls
/// <c>/accounting/company/module-flags</c> on the Accounting API,
/// which already enforces session + company isolation. The list+key
/// reads silently degrade to "no flags" on transport failure so the
/// nav menu treats "unknown" the same as "disabled"; the write fails
/// loud so the Modules settings page can show a real error.
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

    /// <summary>
    /// Settings → Modules page read: returns the full catalog (display
    /// name + description + last-modified metadata) merged with
    /// persisted state. The same backend endpoint as <see cref="GetAsync"/>;
    /// this overload keeps the richer payload so the UI can render the
    /// description column.
    /// </summary>
    public async Task<IReadOnlyList<CompanyModuleFlagSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await httpClient.GetFromJsonAsync<CompanyModuleFlagSummary[]>(
                "accounting/company/module-flags",
                cancellationToken);
            return rows ?? Array.Empty<CompanyModuleFlagSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read company module flags for the Modules settings page.");
            return Array.Empty<CompanyModuleFlagSummary>();
        }
    }

    /// <summary>
    /// Toggle a single catalog module for the active company. Caller
    /// must hold <c>settings.modules.toggle</c>; backend rejects with
    /// 403 otherwise. Reason is stamped on the audit row.
    /// </summary>
    public async Task<ModuleFlagToggleOutcome> SetEnabledAsync(
        string moduleKey,
        bool enabled,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PutAsJsonAsync(
                $"accounting/company/module-flags/{moduleKey}",
                new { Enabled = enabled, Reason = reason },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                return ModuleFlagToggleOutcome.Failure($"{(int)response.StatusCode}: {error}");
            }
            return ModuleFlagToggleOutcome.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Module flag toggle for '{Module}' failed.", moduleKey);
            return ModuleFlagToggleOutcome.Failure(ex.Message);
        }
    }

    private static readonly IReadOnlyDictionary<string, bool> EmptyFlags =
        new Dictionary<string, bool>(StringComparer.Ordinal);
}

public sealed record ModuleFlagToggleOutcome(bool Succeeded, string? ErrorMessage)
{
    public static ModuleFlagToggleOutcome Success() => new(true, null);
    public static ModuleFlagToggleOutcome Failure(string message) => new(false, message);
}
