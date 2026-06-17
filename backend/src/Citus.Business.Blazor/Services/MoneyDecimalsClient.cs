using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company money-decimals setting. The current value
/// is already carried on the session summary (BusinessShellState.MoneyDecimals),
/// so this client only needs the write surface. settings.company.edit gated on
/// the API; failures surface to the settings page.
/// </summary>
public sealed class MoneyDecimalsClient(HttpClient httpClient, ILogger<MoneyDecimalsClient> logger)
{
    public async Task<MoneyDecimalsOutcome> SetAsync(
        int moneyDecimals,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PutAsJsonAsync(
                "accounting/company/settings/money-decimals",
                new { MoneyDecimals = moneyDecimals },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                return MoneyDecimalsOutcome.Failure($"{(int)response.StatusCode}: {error}");
            }
            return MoneyDecimalsOutcome.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Updating company money decimals to {Decimals} failed.", moneyDecimals);
            return MoneyDecimalsOutcome.Failure(ex.Message);
        }
    }
}

public sealed record MoneyDecimalsOutcome(bool Succeeded, string? ErrorMessage)
{
    public static MoneyDecimalsOutcome Success() => new(true, null);
    public static MoneyDecimalsOutcome Failure(string message) => new(false, message);
}
