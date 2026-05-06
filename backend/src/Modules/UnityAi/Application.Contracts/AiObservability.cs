namespace Citus.Modules.UnityAi.Application.Contracts;

/// <summary>
/// Durable record of a background AI / learning run. Created when the run
/// starts; updated to a terminal status when it ends.
/// </summary>
public sealed record AiJobRunRecord(
    Guid Id,
    CompanyId? CompanyId,
    string JobType,
    string Status,
    string TriggerType,
    UserId? TriggeredByUserId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? SourceWindowStart,
    DateTimeOffset? SourceWindowEnd,
    string? InputSummaryJson,
    string? OutputSummaryJson,
    string? ErrorMessage,
    string? WarningsJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AiRequestLogRecord(
    Guid Id,
    CompanyId? CompanyId,
    Guid? JobRunId,
    string TaskType,
    string? Provider,
    string? Model,
    string? RequestSchemaVersion,
    string? ResponseSchemaVersion,
    string? InputHash,
    string? InputRedactedJson,
    string? OutputRedactedJson,
    string Status,
    string? ErrorMessage,
    string? PromptVersion,
    int? TokenInputCount,
    int? TokenOutputCount,
    decimal? EstimatedCost,
    int? LatencyMs,
    DateTimeOffset CreatedAt);

public interface IAiJobRunStore
{
    Task<Guid> StartAsync(
        CompanyId? companyId,
        string jobType,
        string triggerType,
        UserId? triggeredByUserId,
        DateTimeOffset? sourceWindowStart,
        DateTimeOffset? sourceWindowEnd,
        string? inputSummaryJson,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        Guid jobRunId,
        string status,
        string? outputSummaryJson,
        string? errorMessage,
        string? warningsJson,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AiJobRunRecord>> GetRecentAsync(
        CompanyId companyId,
        string? jobType,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Platform-wide variant for the SysAdmin AI Activity page —
    /// returns recent runs across every company so an operator can
    /// audit "what is the AI doing right now" at a glance.
    /// </summary>
    Task<IReadOnlyList<AiJobRunRecord>> GetRecentPlatformAsync(
        int limit,
        CancellationToken cancellationToken);
}

public interface IAiRequestLogStore
{
    Task<Guid> WriteAsync(AiRequestLogRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<AiRequestLogRecord>> GetRecentAsync(
        CompanyId companyId,
        string? taskType,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Platform-wide recent calls for the SysAdmin AI Activity page.</summary>
    Task<IReadOnlyList<AiRequestLogRecord>> GetRecentPlatformAsync(
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Counters + token / cost totals for a rolling window (typically
    /// 24 h, 7 d, or 30 d). Powers the AI activity tile on SysAdmin
    /// Overview and the period-summary cards on the AI Activity page.
    /// </summary>
    Task<AiActivitySummary> GetPlatformSummaryAsync(
        DateTimeOffset windowStart,
        CancellationToken cancellationToken);
}

/// <summary>
/// Aggregated AI activity over a rolling window. Both succeeded and
/// failed counts are exposed so the SysAdmin tile can render a
/// success-rate ratio without re-querying.
/// </summary>
public sealed record AiActivitySummary(
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
