using System.Net.Http.Json;
using Citus.SysAdmin.Blazor.State;
using Citus.Ui.Shared.Control;

namespace Citus.SysAdmin.Blazor.Services;

/// <summary>
/// Wraps the SysAdmin /control/operations/ai-activity endpoint. Backs the
/// Operations → AI Activity page + the AI tile on Overview. Pure read-only;
/// the writes happen on the Accounting API side via UnityAiGateway.
/// </summary>
public sealed class AiActivityClient(
    HttpClient httpClient,
    AppShellState shellState,
    ILogger<AiActivityClient> logger)
{
    public async Task<AiActivityReportDto?> GetAsync(
        string window = "24h",
        int recentLimit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<AiActivityReportDto>(
                $"control/operations/ai-activity?window={window}&recentLimit={recentLimit}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load AI activity.");
            return null;
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
}

public sealed record AiActivityReportDto(
    string Window,
    AiActivitySummaryDto Summary,
    IReadOnlyList<AiRequestLogDto> RecentCalls,
    IReadOnlyList<AiJobRunDto> RecentJobs);

public sealed record AiActivitySummaryDto(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int TotalCalls,
    int SucceededCalls,
    int FailedCalls,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalEstimatedCost,
    int? AvgLatencyMs,
    DateTimeOffset? LastCallAt);

public sealed record AiRequestLogDto(
    Guid Id,
    Guid? CompanyId,
    Guid? JobRunId,
    string TaskType,
    string? Provider,
    string? Model,
    string Status,
    string? ErrorMessage,
    int? TokenInputCount,
    int? TokenOutputCount,
    decimal? EstimatedCost,
    int? LatencyMs,
    DateTimeOffset CreatedAt);

public sealed record AiJobRunDto(
    Guid Id,
    Guid? CompanyId,
    string JobType,
    string Status,
    string TriggerType,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);
