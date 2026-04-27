namespace Citus.Modules.UnityAi.Application.Contracts;

/// <summary>
/// Durable record of a background AI / learning run. Created when the run
/// starts; updated to a terminal status when it ends.
/// </summary>
public sealed record AiJobRunRecord(
    Guid Id,
    Guid? CompanyId,
    string JobType,
    string Status,
    string TriggerType,
    Guid? TriggeredByUserId,
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
    Guid? CompanyId,
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
        Guid? companyId,
        string jobType,
        string triggerType,
        Guid? triggeredByUserId,
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
        Guid companyId,
        string? jobType,
        int limit,
        CancellationToken cancellationToken);
}

public interface IAiRequestLogStore
{
    Task<Guid> WriteAsync(AiRequestLogRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<AiRequestLogRecord>> GetRecentAsync(
        Guid companyId,
        string? taskType,
        int limit,
        CancellationToken cancellationToken);
}
