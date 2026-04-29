using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;

namespace Citus.Modules.UnityAi.Tests;

/// <summary>
/// Lightweight in-memory store impls used across the test suite. They model
/// company isolation by keying every collection on (CompanyId, ...) so a
/// boundary leak shows up as a wrong-company assertion failure.
/// </summary>
internal sealed class InMemoryAiJobRunStore : IAiJobRunStore
{
    public List<AiJobRunRecord> Records { get; } = new();
    public List<(Guid Id, string Status)> Completions { get; } = new();

    public Task<Guid> StartAsync(Guid? companyId, string jobType, string triggerType, Guid? triggeredByUserId,
        DateTimeOffset? sourceWindowStart, DateTimeOffset? sourceWindowEnd, string? inputSummaryJson,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Records.Add(new AiJobRunRecord(id, companyId, jobType, AiJobRunStatus.Running, triggerType, triggeredByUserId,
            now, null, sourceWindowStart, sourceWindowEnd, inputSummaryJson, null, null, null, now, now));
        return Task.FromResult(id);
    }

    public Task CompleteAsync(Guid jobRunId, string status, string? outputSummaryJson, string? errorMessage, string? warningsJson, CancellationToken cancellationToken)
    {
        Completions.Add((jobRunId, status));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AiJobRunRecord>> GetRecentAsync(Guid companyId, string? jobType, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AiJobRunRecord>>(Records.Where(r => r.CompanyId == companyId).ToList());
}

internal sealed class InMemoryAiRequestLogStore : IAiRequestLogStore
{
    public List<AiRequestLogRecord> Records { get; } = new();

    public Task<Guid> WriteAsync(AiRequestLogRecord record, CancellationToken cancellationToken)
    {
        Records.Add(record);
        return Task.FromResult(record.Id);
    }

    public Task<IReadOnlyList<AiRequestLogRecord>> GetRecentAsync(Guid companyId, string? taskType, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AiRequestLogRecord>>(Records.Where(r => r.CompanyId == companyId).ToList());
}

internal sealed class InMemoryUsageStatStore : IUnitysearchUsageStatStore
{
    private readonly List<UnitysearchUsageStatRecord> _records = new();

    public IReadOnlyList<UnitysearchUsageStatRecord> All => _records;

    public Task UpsertOnSelectAsync(Guid companyId, Guid? userId, string context, string entityType, Guid entityId,
        int? rankPosition, string? query, DateTimeOffset selectedAt, CancellationToken cancellationToken)
    {
        Upsert(companyId, null, UnitysearchScopeType.Company, context, entityType, entityId, rankPosition, query, selectedAt);
        if (userId is not null)
        {
            Upsert(companyId, userId, UnitysearchScopeType.User, context, entityType, entityId, rankPosition, query, selectedAt);
        }
        return Task.CompletedTask;
    }

    public void Seed(Guid companyId, Guid? userId, string scopeType, string context, string entityType, Guid entityId,
        int selectCount, DateTimeOffset? lastSelectedAt = null)
    {
        _records.Add(new UnitysearchUsageStatRecord(
            Id: Guid.NewGuid(),
            CompanyId: companyId,
            ScopeType: scopeType,
            UserId: userId,
            Context: context,
            EntityType: entityType,
            EntityId: entityId,
            SelectCount: selectCount,
            SelectCount7d: selectCount,
            SelectCount30d: selectCount,
            SelectCount90d: selectCount,
            LastSelectedAt: lastSelectedAt,
            LastQuery: null,
            AvgRankPosition: null,
            UpdatedAt: DateTimeOffset.UtcNow));
    }

    private void Upsert(Guid companyId, Guid? userId, string scopeType, string context, string entityType, Guid entityId,
        int? rankPosition, string? query, DateTimeOffset selectedAt)
    {
        var existing = _records.FirstOrDefault(r =>
            r.CompanyId == companyId && r.ScopeType == scopeType && r.UserId == userId &&
            r.Context == context && r.EntityType == entityType && r.EntityId == entityId);
        if (existing is null)
        {
            _records.Add(new UnitysearchUsageStatRecord(
                Id: Guid.NewGuid(),
                CompanyId: companyId, ScopeType: scopeType, UserId: userId,
                Context: context, EntityType: entityType, EntityId: entityId,
                SelectCount: 1, SelectCount7d: 1, SelectCount30d: 1, SelectCount90d: 1,
                LastSelectedAt: selectedAt, LastQuery: query, AvgRankPosition: rankPosition,
                UpdatedAt: selectedAt));
        }
        else
        {
            _records.Remove(existing);
            _records.Add(existing with
            {
                SelectCount = existing.SelectCount + 1,
                SelectCount7d = existing.SelectCount7d + 1,
                SelectCount30d = existing.SelectCount30d + 1,
                SelectCount90d = existing.SelectCount90d + 1,
                LastSelectedAt = selectedAt,
                LastQuery = query ?? existing.LastQuery,
                UpdatedAt = selectedAt,
            });
        }
    }

    public Task<IReadOnlyDictionary<Guid, UnitysearchUsageStatRecord>> GetForCandidatesAsync(
        Guid companyId, Guid? userId, string scopeType, string context, string entityType,
        IReadOnlyCollection<Guid> entityIds, CancellationToken cancellationToken)
    {
        var dict = _records
            .Where(r => r.CompanyId == companyId && r.ScopeType == scopeType && r.UserId == userId &&
                        r.Context == context && r.EntityType == entityType && entityIds.Contains(r.EntityId))
            .ToDictionary(r => r.EntityId);
        return Task.FromResult<IReadOnlyDictionary<Guid, UnitysearchUsageStatRecord>>(dict);
    }

    public Task<IReadOnlyList<UnitysearchUsageStatRecord>> GetTopByCompanyScopeAsync(
        Guid companyId, int limit, CancellationToken cancellationToken)
    {
        var results = _records
            .Where(r => r.CompanyId == companyId && r.ScopeType == UnitysearchScopeType.Company)
            .OrderByDescending(r => r.SelectCount30d)
            .ThenByDescending(r => r.SelectCount)
            .Take(Math.Max(0, limit))
            .ToList();
        return Task.FromResult<IReadOnlyList<UnitysearchUsageStatRecord>>(results);
    }
}

internal sealed class InMemoryPairStatStore : IUnitysearchPairStatStore
{
    private readonly List<UnitysearchPairStatRecord> _records = new();

    public Task UpsertOnSelectAsync(Guid companyId, Guid? userId, string sourceContext,
        string anchorEntityType, Guid anchorEntityId, string targetContext,
        string targetEntityType, Guid targetEntityId, DateTimeOffset selectedAt, CancellationToken cancellationToken)
    {
        Upsert(companyId, null, UnitysearchScopeType.Company, sourceContext, anchorEntityType, anchorEntityId, targetContext, targetEntityType, targetEntityId, selectedAt);
        if (userId is not null)
        {
            Upsert(companyId, userId, UnitysearchScopeType.User, sourceContext, anchorEntityType, anchorEntityId, targetContext, targetEntityType, targetEntityId, selectedAt);
        }
        return Task.CompletedTask;
    }

    public void Seed(Guid companyId, Guid? userId, string scopeType, string sourceContext, string anchorEntityType, Guid anchorEntityId,
        string targetContext, string targetEntityType, Guid targetEntityId, decimal confidence, int selectCount = 1)
    {
        _records.Add(new UnitysearchPairStatRecord(
            Id: Guid.NewGuid(), CompanyId: companyId, ScopeType: scopeType, UserId: userId,
            SourceContext: sourceContext, AnchorEntityType: anchorEntityType, AnchorEntityId: anchorEntityId,
            TargetContext: targetContext, TargetEntityType: targetEntityType, TargetEntityId: targetEntityId,
            SelectCount: selectCount, TotalAnchorSelectCount: selectCount, ConfidenceScore: confidence,
            LastSelectedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow));
    }

    private void Upsert(Guid companyId, Guid? userId, string scopeType, string sourceContext,
        string anchorEntityType, Guid anchorEntityId, string targetContext, string targetEntityType, Guid targetEntityId,
        DateTimeOffset selectedAt)
    {
        var existing = _records.FirstOrDefault(r =>
            r.CompanyId == companyId && r.ScopeType == scopeType && r.UserId == userId &&
            r.SourceContext == sourceContext && r.AnchorEntityType == anchorEntityType &&
            r.AnchorEntityId == anchorEntityId && r.TargetContext == targetContext &&
            r.TargetEntityType == targetEntityType && r.TargetEntityId == targetEntityId);
        if (existing is null)
        {
            _records.Add(new UnitysearchPairStatRecord(
                Id: Guid.NewGuid(), CompanyId: companyId, ScopeType: scopeType, UserId: userId,
                SourceContext: sourceContext, AnchorEntityType: anchorEntityType, AnchorEntityId: anchorEntityId,
                TargetContext: targetContext, TargetEntityType: targetEntityType, TargetEntityId: targetEntityId,
                SelectCount: 1, TotalAnchorSelectCount: 1, ConfidenceScore: 1m,
                LastSelectedAt: selectedAt, UpdatedAt: selectedAt));
        }
        else
        {
            _records.Remove(existing);
            var newSelect = existing.SelectCount + 1;
            var newTotal = existing.TotalAnchorSelectCount + 1;
            _records.Add(existing with
            {
                SelectCount = newSelect,
                TotalAnchorSelectCount = newTotal,
                ConfidenceScore = Math.Min(1m, (decimal)newSelect / newTotal),
                LastSelectedAt = selectedAt,
                UpdatedAt = selectedAt,
            });
        }
    }

    public Task<IReadOnlyList<UnitysearchPairStatRecord>> GetForAnchorAsync(
        Guid companyId, Guid? userId, string scopeType, string sourceContext,
        string anchorEntityType, Guid anchorEntityId, string targetContext, string targetEntityType,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<UnitysearchPairStatRecord>>(_records.Where(r =>
            r.CompanyId == companyId && r.ScopeType == scopeType && r.UserId == userId &&
            r.SourceContext == sourceContext && r.AnchorEntityType == anchorEntityType &&
            r.AnchorEntityId == anchorEntityId && r.TargetContext == targetContext &&
            r.TargetEntityType == targetEntityType).ToList());
}

internal sealed class InMemoryRankingHintStore : IUnitysearchRankingHintStore
{
    private readonly List<UnitysearchRankingHintRecord> _records = new();

    public void Seed(UnitysearchRankingHintRecord record) => _records.Add(record);

    public Task<IReadOnlyList<UnitysearchRankingHintRecord>> GetActiveAsync(
        Guid companyId, Guid? userId, string context, string entityType,
        IReadOnlyCollection<Guid>? entityIds, CancellationToken cancellationToken)
    {
        var query = _records.Where(r => r.CompanyId == companyId && r.Context == context && r.EntityType == entityType);
        if (entityIds is not null && entityIds.Count > 0)
        {
            query = query.Where(r => entityIds.Contains(r.EntityId));
        }
        // Service layer filters by status/validation; the engine double-checks.
        return Task.FromResult<IReadOnlyList<UnitysearchRankingHintRecord>>(query.ToList());
    }

    public Task UpsertAsync(UnitysearchRankingHintRecord record, CancellationToken cancellationToken)
    {
        _records.Add(record);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryActionCenterTaskStore : IActionCenterTaskStore
{
    public List<ActionCenterTaskRecord> Records { get; } = new();

    public Task<ActionCenterTaskRecord?> GetByIdAsync(Guid companyId, Guid taskId, CancellationToken cancellationToken)
        => Task.FromResult(Records.FirstOrDefault(r => r.CompanyId == companyId && r.Id == taskId));

    public Task<ActionCenterTaskRecord?> GetByFingerprintAsync(Guid companyId, string fingerprint, CancellationToken cancellationToken)
        => Task.FromResult(Records.FirstOrDefault(r => r.CompanyId == companyId && r.Fingerprint == fingerprint));

    public Task<IReadOnlyList<ActionCenterTaskRecord>> GetTasksAsync(
        Guid companyId, Guid? assignedUserId, IReadOnlyCollection<string>? statuses, CancellationToken cancellationToken)
    {
        var q = Records.Where(r => r.CompanyId == companyId);
        if (assignedUserId is not null) q = q.Where(r => r.AssignedUserId == assignedUserId || r.AssignedUserId is null);
        if (statuses is not null && statuses.Count > 0) q = q.Where(r => statuses.Contains(r.Status));
        return Task.FromResult<IReadOnlyList<ActionCenterTaskRecord>>(q.ToList());
    }

    public Task<Guid> InsertAsync(ActionCenterTaskRecord record, CancellationToken cancellationToken)
    {
        Records.Add(record);
        return Task.FromResult(record.Id);
    }

    public Task UpdateStatusAsync(Guid companyId, Guid taskId, string status,
        DateTimeOffset? completedAt, DateTimeOffset? dismissedAt, DateTimeOffset? snoozedUntil,
        DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        var idx = Records.FindIndex(r => r.CompanyId == companyId && r.Id == taskId);
        if (idx >= 0)
        {
            Records[idx] = Records[idx] with
            {
                Status = status,
                CompletedAt = completedAt,
                DismissedAt = dismissedAt,
                SnoozedUntil = snoozedUntil,
                UpdatedAt = updatedAt,
            };
        }
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryActionCenterTaskEventStore : IActionCenterTaskEventStore
{
    public List<(Guid CompanyId, Guid TaskId, Guid? UserId, string EventType, DateTimeOffset At)> Events { get; } = new();

    public Task RecordAsync(Guid companyId, Guid taskId, Guid? userId, string eventType, string? metadataJson,
        DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        Events.Add((companyId, taskId, userId, eventType, occurredAt));
        return Task.CompletedTask;
    }
}
