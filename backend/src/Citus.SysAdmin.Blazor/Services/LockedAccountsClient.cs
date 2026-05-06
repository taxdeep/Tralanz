using System.Net.Http.Json;
using Citus.SysAdmin.Blazor.State;
using Citus.Ui.Shared.Control;

namespace Citus.SysAdmin.Blazor.Services;

/// <summary>
/// Wraps the SysAdmin /control/operations/lockouts endpoints. Backs the
/// Locked Accounts page — list active lockouts + manual lift.
/// </summary>
public sealed class LockedAccountsClient(
    HttpClient httpClient,
    AppShellState shellState,
    ILogger<LockedAccountsClient> logger)
{
    public async Task<IReadOnlyList<LockoutSummaryDto>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            var rows = await httpClient.GetFromJsonAsync<List<LockoutSummaryDto>>(
                "control/operations/lockouts", cancellationToken);
            return rows ?? new List<LockoutSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list active lockouts.");
            return Array.Empty<LockoutSummaryDto>();
        }
    }

    public async Task<LockoutLiftOutcome> LiftAsync(
        Guid lockoutId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PostAsJsonAsync(
                $"control/operations/lockouts/{lockoutId:D}/lift",
                new { Reason = reason },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return new LockoutLiftOutcome(false, error);
            }

            return new LockoutLiftOutcome(true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to lift lockout {LockoutId}.", lockoutId);
            return new LockoutLiftOutcome(false, ex.Message);
        }
    }

    private void ApplySessionHeader()
    {
        httpClient.DefaultRequestHeaders.Remove(SysAdminAuthConstants.SessionHeaderName);
        if (shellState.IsAuthenticated)
        {
            httpClient.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, shellState.SessionToken);
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken);
            return body?.Message ?? $"Server returned {(int)response.StatusCode}.";
        }
        catch
        {
            return $"Server returned {(int)response.StatusCode}.";
        }
    }

    private sealed record ErrorBody(string Message);
}

public sealed record LockoutSummaryDto(
    Guid Id,
    string Realm,
    string MaskedEmail,
    UserId? AccountId,
    string LockoutKind,
    DateTimeOffset LockedAt,
    DateTimeOffset? LockedUntil,
    int RecentFailureCount);

public sealed record LockoutLiftOutcome(bool Succeeded, string? ErrorMessage);
