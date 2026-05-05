namespace Citus.Modules.UnityAi.Application.Contracts;

public sealed record UnitysearchEventInput(
    CompanyId CompanyId,
    Guid? UserId,
    string? SessionId,
    string Context,
    string EntityType,
    string? Query,
    string? NormalizedQuery,
    string EventType,
    Guid? SelectedEntityId,
    int? RankPosition,
    int? ResultCount,
    string? SourceRoute,
    string? AnchorContext,
    string? AnchorEntityType,
    Guid? AnchorEntityId,
    string? MetadataJson);

public sealed record UnitysearchUsageStatRecord(
    Guid Id,
    CompanyId CompanyId,
    string ScopeType,
    Guid? UserId,
    string Context,
    string EntityType,
    Guid EntityId,
    int SelectCount,
    int SelectCount7d,
    int SelectCount30d,
    int SelectCount90d,
    DateTimeOffset? LastSelectedAt,
    string? LastQuery,
    decimal? AvgRankPosition,
    DateTimeOffset UpdatedAt);

public sealed record UnitysearchPairStatRecord(
    Guid Id,
    CompanyId CompanyId,
    string ScopeType,
    Guid? UserId,
    string SourceContext,
    string AnchorEntityType,
    Guid AnchorEntityId,
    string TargetContext,
    string TargetEntityType,
    Guid TargetEntityId,
    int SelectCount,
    int TotalAnchorSelectCount,
    decimal ConfidenceScore,
    DateTimeOffset? LastSelectedAt,
    DateTimeOffset UpdatedAt);

public sealed record UnitysearchRankingHintRecord(
    Guid Id,
    CompanyId CompanyId,
    Guid? UserId,
    string Context,
    string EntityType,
    Guid EntityId,
    decimal BoostScore,
    decimal Confidence,
    string? Reason,
    string Source,
    string Status,
    string ValidationStatus,
    DateTimeOffset? ExpiresAt);

public interface IUnitysearchEventStore
{
    Task RecordEventAsync(UnitysearchEventInput input, CancellationToken cancellationToken);
}

public interface IUnitysearchUsageStatStore
{
    Task UpsertOnSelectAsync(
        CompanyId companyId,
        UserId? userId,
        string context,
        string entityType,
        Guid entityId,
        int? rankPosition,
        string? query,
        DateTimeOffset selectedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, UnitysearchUsageStatRecord>> GetForCandidatesAsync(
        CompanyId companyId,
        UserId? userId,
        string scopeType,
        string context,
        string entityType,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most-clicked (context, entity_type, entity_id) rows for a
    /// company, ordered by 30-day select count desc. Used by the AI hint
    /// distillation flow to discover entities worth asking the LLM about —
    /// the existing <see cref="GetForCandidatesAsync"/> requires the caller
    /// to already know the entity IDs, which is the wrong shape for
    /// discovery.
    /// </summary>
    Task<IReadOnlyList<UnitysearchUsageStatRecord>> GetTopByCompanyScopeAsync(
        CompanyId companyId,
        int limit,
        CancellationToken cancellationToken);
}

public interface IUnitysearchPairStatStore
{
    Task UpsertOnSelectAsync(
        CompanyId companyId,
        UserId? userId,
        string sourceContext,
        string anchorEntityType,
        Guid anchorEntityId,
        string targetContext,
        string targetEntityType,
        Guid targetEntityId,
        DateTimeOffset selectedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UnitysearchPairStatRecord>> GetForAnchorAsync(
        CompanyId companyId,
        UserId? userId,
        string scopeType,
        string sourceContext,
        string anchorEntityType,
        Guid anchorEntityId,
        string targetContext,
        string targetEntityType,
        CancellationToken cancellationToken);
}

public interface IUnitysearchRecentQueryStore
{
    Task RecordAsync(
        CompanyId companyId,
        UserId? userId,
        string context,
        string query,
        string normalizedQuery,
        bool resultClicked,
        string? clickedEntityType,
        Guid? clickedEntityId,
        int? resultCount,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken);
}

public interface IUnitysearchRankingHintStore
{
    Task<IReadOnlyList<UnitysearchRankingHintRecord>> GetActiveAsync(
        CompanyId companyId,
        UserId? userId,
        string context,
        string entityType,
        IReadOnlyCollection<Guid>? entityIds,
        CancellationToken cancellationToken);

    Task UpsertAsync(
        UnitysearchRankingHintRecord record,
        CancellationToken cancellationToken);
}
