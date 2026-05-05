namespace Citus.Modules.UnityAi.Application.Contracts;

public sealed record ActionCenterTaskRecord(
    Guid Id,
    CompanyId CompanyId,
    UserId? AssignedUserId,
    string TaskType,
    string SourceEngine,
    string SourceType,
    Guid? SourceObjectId,
    string Title,
    string? Description,
    string Reason,
    string? EvidenceJson,
    string Priority,
    DateOnly? DueDate,
    string? ActionUrl,
    string Status,
    string Fingerprint,
    bool AiGenerated,
    decimal? Confidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? DismissedAt,
    DateTimeOffset? SnoozedUntil);

/// <summary>
/// Provider-emitted draft, before insertion. The fingerprint must be
/// idempotent for the underlying state — re-running the provider on
/// unchanged data must produce the same fingerprint.
/// </summary>
public sealed record ActionCenterTaskDraft(
    CompanyId CompanyId,
    UserId? AssignedUserId,
    string TaskType,
    string SourceEngine,
    string SourceType,
    Guid? SourceObjectId,
    string Title,
    string? Description,
    string Reason,
    string? EvidenceJson,
    string Priority,
    DateOnly? DueDate,
    string? ActionUrl,
    string Fingerprint,
    bool AiGenerated = false,
    decimal? Confidence = null);

/// <summary>
/// Implemented per task domain (AR overdue, AP due-soon, banking, system
/// setup, sales tax). Providers may return zero results — they must NOT
/// fabricate tasks when the underlying domain data is unavailable.
/// </summary>
public interface IActionCenterTaskProvider
{
    string ProviderName { get; }

    Task<IReadOnlyList<ActionCenterTaskDraft>> GenerateAsync(
        CompanyId companyId,
        UserId? userId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken);
}

public sealed record ActionCenterGenerationResult(
    int Inserted,
    int Updated,
    int Deduped,
    IReadOnlyList<string> ProviderWarnings);

public interface IActionCenterTaskService
{
    Task<ActionCenterGenerationResult> RegenerateAsync(
        CompanyId companyId,
        UserId? userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionCenterTaskRecord>> GetTasksAsync(
        CompanyId companyId,
        UserId? assignedUserId,
        IReadOnlyCollection<string>? statuses,
        CancellationToken cancellationToken);

    Task<ActionCenterTaskRecord?> StartAsync(CompanyId companyId, Guid taskId, UserId? actorUserId, CancellationToken cancellationToken);
    Task<ActionCenterTaskRecord?> CompleteAsync(CompanyId companyId, Guid taskId, UserId? actorUserId, CancellationToken cancellationToken);
    Task<ActionCenterTaskRecord?> DismissAsync(CompanyId companyId, Guid taskId, UserId? actorUserId, CancellationToken cancellationToken);
    Task<ActionCenterTaskRecord?> SnoozeAsync(CompanyId companyId, Guid taskId, UserId? actorUserId, DateTimeOffset until, CancellationToken cancellationToken);
}

public interface IActionCenterTaskStore
{
    Task<ActionCenterTaskRecord?> GetByIdAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken);

    Task<ActionCenterTaskRecord?> GetByFingerprintAsync(CompanyId companyId, string fingerprint, CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionCenterTaskRecord>> GetTasksAsync(
        CompanyId companyId,
        UserId? assignedUserId,
        IReadOnlyCollection<string>? statuses,
        CancellationToken cancellationToken);

    Task<Guid> InsertAsync(ActionCenterTaskRecord record, CancellationToken cancellationToken);

    Task UpdateStatusAsync(
        CompanyId companyId,
        Guid taskId,
        string status,
        DateTimeOffset? completedAt,
        DateTimeOffset? dismissedAt,
        DateTimeOffset? snoozedUntil,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
}

public interface IActionCenterTaskEventStore
{
    Task RecordAsync(
        CompanyId companyId,
        Guid taskId,
        UserId? userId,
        string eventType,
        string? metadataJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);
}
