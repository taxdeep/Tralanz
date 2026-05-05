using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// M7 iter 1: HTTP client for the accounting period state machine.
/// List degrades gracefully (empty list on error); transition surfaces
/// a user-displayable message on failure so the workbench can keep
/// running across transient or authority-rejection responses.
/// </summary>
public sealed class AccountingPeriodClient(HttpClient httpClient, ILogger<AccountingPeriodClient> logger)
{
    public async Task<IReadOnlyList<AccountingPeriodDto>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await httpClient.GetFromJsonAsync<AccountingPeriodDto[]>(
                "accounting/periods", cancellationToken);
            return rows ?? Array.Empty<AccountingPeriodDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read accounting periods.");
            return Array.Empty<AccountingPeriodDto>();
        }
    }

    public async Task<AccountingPeriodTransitionOutcome> TransitionAsync(
        Guid periodId,
        string targetStatus,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/periods/{periodId:D}/transition",
                new { TargetStatus = targetStatus },
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AccountingPeriodTransitionOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<AccountingPeriodDto>(cancellationToken);
            return new AccountingPeriodTransitionOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to transition accounting period {PeriodId}.", periodId);
            return new AccountingPeriodTransitionOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return $"Request failed with status code {(int)response.StatusCode}.";
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString() ?? raw;
            }
        }
        catch (JsonException) { }
        return raw;
    }
}

public sealed record AccountingPeriodDto(
    Guid Id,
    Guid CompanyId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Status,
    DateTimeOffset? ClosingStartedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? LockedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AccountingPeriodTransitionOutcome(
    bool Succeeded,
    AccountingPeriodDto? Saved,
    string? ErrorMessage);
