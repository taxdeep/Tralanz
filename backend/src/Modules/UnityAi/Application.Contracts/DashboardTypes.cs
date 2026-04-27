namespace Citus.Modules.UnityAi.Application.Contracts;

public sealed record DashboardUserWidgetRecord(
    Guid Id,
    Guid CompanyId,
    Guid? UserId,
    string WidgetKey,
    string? Title,
    string? ConfigJson,
    int? Position,
    string Source,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DashboardWidgetSuggestionRecord(
    Guid Id,
    Guid CompanyId,
    Guid? UserId,
    string WidgetKey,
    string Title,
    string Reason,
    string? EvidenceJson,
    decimal Confidence,
    string Source,
    string Status,
    Guid? JobRunId,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? DismissedAt,
    DateTimeOffset? SnoozedUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IDashboardUserWidgetStore
{
    Task<IReadOnlyList<DashboardUserWidgetRecord>> GetActiveAsync(
        Guid companyId,
        Guid? userId,
        CancellationToken cancellationToken);

    Task UpsertAsync(DashboardUserWidgetRecord record, CancellationToken cancellationToken);
}

public interface IDashboardWidgetSuggestionStore
{
    Task<DashboardWidgetSuggestionRecord?> GetByIdAsync(
        Guid companyId,
        Guid suggestionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DashboardWidgetSuggestionRecord>> GetForUserAsync(
        Guid companyId,
        Guid? userId,
        string? statusFilter,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DashboardWidgetSuggestionRecord>> GetExistingForWidgetKeysAsync(
        Guid companyId,
        Guid? userId,
        IReadOnlyCollection<string> widgetKeys,
        CancellationToken cancellationToken);

    Task<Guid> InsertAsync(DashboardWidgetSuggestionRecord record, CancellationToken cancellationToken);

    Task UpdateStatusAsync(
        Guid suggestionId,
        string status,
        DateTimeOffset? acceptedAt,
        DateTimeOffset? dismissedAt,
        DateTimeOffset? snoozedUntil,
        CancellationToken cancellationToken);
}

public sealed record DashboardSuggestionGenerationResult(
    int Generated,
    int SkippedAlreadyActive,
    int SkippedAlreadySuggested);

public interface IDashboardSuggestionService
{
    Task<DashboardSuggestionGenerationResult> GenerateAsync(
        Guid companyId,
        Guid? userId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);
}
