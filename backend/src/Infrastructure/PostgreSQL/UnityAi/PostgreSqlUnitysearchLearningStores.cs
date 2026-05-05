using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.UnityAi;

public sealed class PostgreSqlUnitysearchEventStore(PostgreSqlConnectionFactory connections) : IUnitysearchEventStore
{
    public async Task RecordEventAsync(UnitysearchEventInput input, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO unitysearch_events (
                company_id, user_id, session_id, context, entity_type, query, normalized_query,
                event_type, selected_entity_id, rank_position, result_count, source_route,
                anchor_context, anchor_entity_type, anchor_entity_id, metadata_json)
            VALUES (
                @company_id, @user_id, @session_id, @context, @entity_type, @query, @normalized_query,
                @event_type, @selected_entity_id, @rank_position, @result_count, @source_route,
                @anchor_context, @anchor_entity_type, @anchor_entity_id, @metadata_json::jsonb);
            """;
        command.Parameters.AddWithValue("company_id", input.CompanyId);
        command.Parameters.AddWithValue("user_id", (object?)input.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("session_id", (object?)input.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("context", input.Context);
        command.Parameters.AddWithValue("entity_type", input.EntityType);
        command.Parameters.AddWithValue("query", (object?)input.Query ?? DBNull.Value);
        command.Parameters.AddWithValue("normalized_query", (object?)input.NormalizedQuery ?? DBNull.Value);
        command.Parameters.AddWithValue("event_type", input.EventType);
        command.Parameters.AddWithValue("selected_entity_id", (object?)input.SelectedEntityId ?? DBNull.Value);
        command.Parameters.AddWithValue("rank_position", (object?)input.RankPosition ?? DBNull.Value);
        command.Parameters.AddWithValue("result_count", (object?)input.ResultCount ?? DBNull.Value);
        command.Parameters.AddWithValue("source_route", (object?)input.SourceRoute ?? DBNull.Value);
        command.Parameters.AddWithValue("anchor_context", (object?)input.AnchorContext ?? DBNull.Value);
        command.Parameters.AddWithValue("anchor_entity_type", (object?)input.AnchorEntityType ?? DBNull.Value);
        command.Parameters.AddWithValue("anchor_entity_id", (object?)input.AnchorEntityId ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("metadata_json", NpgsqlDbType.Jsonb) { Value = (object?)input.MetadataJson ?? DBNull.Value });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgreSqlUnitysearchUsageStatStore(PostgreSqlConnectionFactory connections) : IUnitysearchUsageStatStore
{
    public async Task UpsertOnSelectAsync(
        CompanyId companyId, UserId? userId, string context, string entityType, Guid entityId,
        int? rankPosition, string? query, DateTimeOffset selectedAt, CancellationToken cancellationToken)
    {
        // Upsert two rows: company-scope and (when user provided) user-scope.
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        await UpsertOneAsync(connection, companyId, null, UnitysearchScopeType.Company, context, entityType, entityId, rankPosition, query, selectedAt, cancellationToken).ConfigureAwait(false);
        if (userId is not null)
        {
            await UpsertOneAsync(connection, companyId, userId, UnitysearchScopeType.User, context, entityType, entityId, rankPosition, query, selectedAt, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertOneAsync(
        NpgsqlConnection connection,
        CompanyId companyId, UserId? userId, string scopeType,
        string context, string entityType, Guid entityId,
        int? rankPosition, string? query, DateTimeOffset selectedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO unitysearch_usage_stats (
                company_id, scope_type, user_id, context, entity_type, entity_id,
                select_count, last_selected_at, last_query, avg_rank_position, updated_at)
            VALUES (
                @company_id, @scope_type, @user_id, @context, @entity_type, @entity_id,
                1, @selected_at, @query, @avg_rank, @updated_at)
            ON CONFLICT (company_id, scope_type, COALESCE(user_id, '00000000-0000-0000-0000-000000000000'), context, entity_type, entity_id)
            DO UPDATE SET
                select_count = unitysearch_usage_stats.select_count + 1,
                last_selected_at = EXCLUDED.last_selected_at,
                last_query = COALESCE(EXCLUDED.last_query, unitysearch_usage_stats.last_query),
                avg_rank_position = CASE
                    WHEN EXCLUDED.avg_rank_position IS NULL THEN unitysearch_usage_stats.avg_rank_position
                    WHEN unitysearch_usage_stats.avg_rank_position IS NULL THEN EXCLUDED.avg_rank_position
                    ELSE (unitysearch_usage_stats.avg_rank_position * unitysearch_usage_stats.select_count + EXCLUDED.avg_rank_position)
                       / (unitysearch_usage_stats.select_count + 1)
                END,
                updated_at = EXCLUDED.updated_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("scope_type", scopeType);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("context", context);
        command.Parameters.AddWithValue("entity_type", entityType);
        command.Parameters.AddWithValue("entity_id", entityId);
        command.Parameters.AddWithValue("selected_at", selectedAt);
        command.Parameters.AddWithValue("query", (object?)query ?? DBNull.Value);
        command.Parameters.AddWithValue("avg_rank", (object?)(rankPosition is null ? null : (decimal?)rankPosition.Value) ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_at", selectedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, UnitysearchUsageStatRecord>> GetForCandidatesAsync(
        CompanyId companyId, UserId? userId, string scopeType,
        string context, string entityType,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken cancellationToken)
    {
        if (entityIds.Count == 0)
        {
            return new Dictionary<Guid, UnitysearchUsageStatRecord>();
        }

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, company_id, scope_type, user_id, context, entity_type, entity_id,
                   select_count, select_count_7d, select_count_30d, select_count_90d,
                   last_selected_at, last_query, avg_rank_position, updated_at
            FROM unitysearch_usage_stats
            WHERE company_id = @company_id
              AND scope_type = @scope_type
              AND COALESCE(user_id, '00000000-0000-0000-0000-000000000000') = COALESCE(@user_id, '00000000-0000-0000-0000-000000000000')
              AND context = @context
              AND entity_type = @entity_type
              AND entity_id = ANY(@entity_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("scope_type", scopeType);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("context", context);
        command.Parameters.AddWithValue("entity_type", entityType);
        command.Parameters.Add(new NpgsqlParameter("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = entityIds.ToArray() });

        var dict = new Dictionary<Guid, UnitysearchUsageStatRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entityId = reader.GetGuid(6);
            dict[entityId] = new UnitysearchUsageStatRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.GetGuid(1),
                ScopeType: reader.GetString(2),
                UserId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                Context: reader.GetString(4),
                EntityType: reader.GetString(5),
                EntityId: entityId,
                SelectCount: reader.GetInt32(7),
                SelectCount7d: reader.GetInt32(8),
                SelectCount30d: reader.GetInt32(9),
                SelectCount90d: reader.GetInt32(10),
                LastSelectedAt: reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                LastQuery: reader.IsDBNull(12) ? null : reader.GetString(12),
                AvgRankPosition: reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(14));
        }
        return dict;
    }

    public async Task<IReadOnlyList<UnitysearchUsageStatRecord>> GetTopByCompanyScopeAsync(
        CompanyId companyId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return Array.Empty<UnitysearchUsageStatRecord>();
        }

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, company_id, scope_type, user_id, context, entity_type, entity_id,
                   select_count, select_count_7d, select_count_30d, select_count_90d,
                   last_selected_at, last_query, avg_rank_position, updated_at
            FROM unitysearch_usage_stats
            WHERE company_id = @company_id
              AND scope_type = @scope_type
            ORDER BY select_count_30d DESC NULLS LAST,
                     select_count DESC,
                     last_selected_at DESC NULLS LAST
            LIMIT @lim;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("scope_type", UnitysearchScopeType.Company);
        command.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 500));

        var results = new List<UnitysearchUsageStatRecord>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new UnitysearchUsageStatRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.GetGuid(1),
                ScopeType: reader.GetString(2),
                UserId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                Context: reader.GetString(4),
                EntityType: reader.GetString(5),
                EntityId: reader.GetGuid(6),
                SelectCount: reader.GetInt32(7),
                SelectCount7d: reader.GetInt32(8),
                SelectCount30d: reader.GetInt32(9),
                SelectCount90d: reader.GetInt32(10),
                LastSelectedAt: reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                LastQuery: reader.IsDBNull(12) ? null : reader.GetString(12),
                AvgRankPosition: reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(14)));
        }
        return results;
    }
}

public sealed class PostgreSqlUnitysearchPairStatStore(PostgreSqlConnectionFactory connections) : IUnitysearchPairStatStore
{
    public async Task UpsertOnSelectAsync(
        CompanyId companyId, UserId? userId,
        string sourceContext, string anchorEntityType, Guid anchorEntityId,
        string targetContext, string targetEntityType, Guid targetEntityId,
        DateTimeOffset selectedAt, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        await UpsertOneAsync(connection, companyId, null, UnitysearchScopeType.Company, sourceContext, anchorEntityType, anchorEntityId, targetContext, targetEntityType, targetEntityId, selectedAt, cancellationToken).ConfigureAwait(false);
        if (userId is not null)
        {
            await UpsertOneAsync(connection, companyId, userId, UnitysearchScopeType.User, sourceContext, anchorEntityType, anchorEntityId, targetContext, targetEntityType, targetEntityId, selectedAt, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertOneAsync(
        NpgsqlConnection connection,
        CompanyId companyId, UserId? userId, string scopeType,
        string sourceContext, string anchorEntityType, Guid anchorEntityId,
        string targetContext, string targetEntityType, Guid targetEntityId,
        DateTimeOffset selectedAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO unitysearch_pair_stats (
                company_id, scope_type, user_id, source_context, anchor_entity_type, anchor_entity_id,
                target_context, target_entity_type, target_entity_id,
                select_count, total_anchor_select_count, confidence_score,
                last_selected_at, updated_at)
            VALUES (
                @company_id, @scope_type, @user_id, @source_context, @anchor_entity_type, @anchor_entity_id,
                @target_context, @target_entity_type, @target_entity_id,
                1, 1, 1.0, @selected_at, @selected_at)
            ON CONFLICT (company_id, scope_type, COALESCE(user_id, '00000000-0000-0000-0000-000000000000'), source_context, anchor_entity_type, anchor_entity_id, target_context, target_entity_type, target_entity_id)
            DO UPDATE SET
                select_count = unitysearch_pair_stats.select_count + 1,
                total_anchor_select_count = unitysearch_pair_stats.total_anchor_select_count + 1,
                confidence_score = LEAST(1.0, (unitysearch_pair_stats.select_count + 1)::numeric / GREATEST(1, unitysearch_pair_stats.total_anchor_select_count + 1)),
                last_selected_at = EXCLUDED.last_selected_at,
                updated_at = EXCLUDED.updated_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("scope_type", scopeType);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("source_context", sourceContext);
        command.Parameters.AddWithValue("anchor_entity_type", anchorEntityType);
        command.Parameters.AddWithValue("anchor_entity_id", anchorEntityId);
        command.Parameters.AddWithValue("target_context", targetContext);
        command.Parameters.AddWithValue("target_entity_type", targetEntityType);
        command.Parameters.AddWithValue("target_entity_id", targetEntityId);
        command.Parameters.AddWithValue("selected_at", selectedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UnitysearchPairStatRecord>> GetForAnchorAsync(
        CompanyId companyId, UserId? userId, string scopeType,
        string sourceContext, string anchorEntityType, Guid anchorEntityId,
        string targetContext, string targetEntityType,
        CancellationToken cancellationToken)
    {
        var items = new List<UnitysearchPairStatRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, company_id, scope_type, user_id, source_context, anchor_entity_type, anchor_entity_id,
                   target_context, target_entity_type, target_entity_id,
                   select_count, total_anchor_select_count, confidence_score,
                   last_selected_at, updated_at
            FROM unitysearch_pair_stats
            WHERE company_id = @company_id
              AND scope_type = @scope_type
              AND COALESCE(user_id, '00000000-0000-0000-0000-000000000000') = COALESCE(@user_id, '00000000-0000-0000-0000-000000000000')
              AND source_context = @source_context
              AND anchor_entity_type = @anchor_entity_type
              AND anchor_entity_id = @anchor_entity_id
              AND target_context = @target_context
              AND target_entity_type = @target_entity_type;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("scope_type", scopeType);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("source_context", sourceContext);
        command.Parameters.AddWithValue("anchor_entity_type", anchorEntityType);
        command.Parameters.AddWithValue("anchor_entity_id", anchorEntityId);
        command.Parameters.AddWithValue("target_context", targetContext);
        command.Parameters.AddWithValue("target_entity_type", targetEntityType);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new UnitysearchPairStatRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.GetGuid(1),
                ScopeType: reader.GetString(2),
                UserId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                SourceContext: reader.GetString(4),
                AnchorEntityType: reader.GetString(5),
                AnchorEntityId: reader.GetGuid(6),
                TargetContext: reader.GetString(7),
                TargetEntityType: reader.GetString(8),
                TargetEntityId: reader.GetGuid(9),
                SelectCount: reader.GetInt32(10),
                TotalAnchorSelectCount: reader.GetInt32(11),
                ConfidenceScore: reader.GetDecimal(12),
                LastSelectedAt: reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(14)));
        }
        return items;
    }
}

public sealed class PostgreSqlUnitysearchRecentQueryStore(PostgreSqlConnectionFactory connections) : IUnitysearchRecentQueryStore
{
    public async Task RecordAsync(
        CompanyId companyId, UserId? userId, string context, string query, string normalizedQuery,
        bool resultClicked, string? clickedEntityType, Guid? clickedEntityId, int? resultCount,
        DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO unitysearch_recent_queries (
                company_id, user_id, context, query, normalized_query,
                result_clicked, clicked_entity_type, clicked_entity_id, result_count, created_at)
            VALUES (
                @company_id, @user_id, @context, @query, @normalized_query,
                @result_clicked, @clicked_entity_type, @clicked_entity_id, @result_count, @created_at);
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("context", context);
        command.Parameters.AddWithValue("query", query);
        command.Parameters.AddWithValue("normalized_query", normalizedQuery);
        command.Parameters.AddWithValue("result_clicked", resultClicked);
        command.Parameters.AddWithValue("clicked_entity_type", (object?)clickedEntityType ?? DBNull.Value);
        command.Parameters.AddWithValue("clicked_entity_id", (object?)clickedEntityId ?? DBNull.Value);
        command.Parameters.AddWithValue("result_count", (object?)resultCount ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgreSqlUnitysearchRankingHintStore(PostgreSqlConnectionFactory connections) : IUnitysearchRankingHintStore
{
    public async Task<IReadOnlyList<UnitysearchRankingHintRecord>> GetActiveAsync(
        CompanyId companyId, UserId? userId, string context, string entityType,
        IReadOnlyCollection<Guid>? entityIds, CancellationToken cancellationToken)
    {
        var items = new List<UnitysearchRankingHintRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = entityIds is null || entityIds.Count == 0
            ? """
              SELECT id, company_id, user_id, context, entity_type, entity_id,
                     boost_score, confidence, reason, source, status, validation_status, expires_at
              FROM unitysearch_ranking_hints
              WHERE company_id = @company_id
                AND context = @context
                AND entity_type = @entity_type
                AND status = 'active'
                AND validation_status = 'valid'
                AND (expires_at IS NULL OR expires_at > NOW());
              """
            : """
              SELECT id, company_id, user_id, context, entity_type, entity_id,
                     boost_score, confidence, reason, source, status, validation_status, expires_at
              FROM unitysearch_ranking_hints
              WHERE company_id = @company_id
                AND context = @context
                AND entity_type = @entity_type
                AND entity_id = ANY(@entity_ids)
                AND status = 'active'
                AND validation_status = 'valid'
                AND (expires_at IS NULL OR expires_at > NOW());
              """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("context", context);
        command.Parameters.AddWithValue("entity_type", entityType);
        if (entityIds is not null && entityIds.Count > 0)
        {
            command.Parameters.Add(new NpgsqlParameter("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = entityIds.ToArray() });
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new UnitysearchRankingHintRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.GetGuid(1),
                UserId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                Context: reader.GetString(3),
                EntityType: reader.GetString(4),
                EntityId: reader.GetGuid(5),
                BoostScore: reader.GetDecimal(6),
                Confidence: reader.GetDecimal(7),
                Reason: reader.IsDBNull(8) ? null : reader.GetString(8),
                Source: reader.GetString(9),
                Status: reader.GetString(10),
                ValidationStatus: reader.GetString(11),
                ExpiresAt: reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12)));
        }
        return items;
    }

    public async Task UpsertAsync(UnitysearchRankingHintRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO unitysearch_ranking_hints (
                id, company_id, user_id, context, entity_type, entity_id,
                boost_score, confidence, reason, source, status, validation_status, expires_at)
            VALUES (
                @id, @company_id, @user_id, @context, @entity_type, @entity_id,
                @boost_score, @confidence, @reason, @source, @status, @validation_status, @expires_at)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("id", record.Id == Guid.Empty ? Guid.NewGuid() : record.Id);
        command.Parameters.AddWithValue("company_id", record.CompanyId);
        command.Parameters.AddWithValue("user_id", (object?)record.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("context", record.Context);
        command.Parameters.AddWithValue("entity_type", record.EntityType);
        command.Parameters.AddWithValue("entity_id", record.EntityId);
        command.Parameters.AddWithValue("boost_score", record.BoostScore);
        command.Parameters.AddWithValue("confidence", record.Confidence);
        command.Parameters.AddWithValue("reason", (object?)record.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("source", record.Source);
        command.Parameters.AddWithValue("status", record.Status);
        command.Parameters.AddWithValue("validation_status", record.ValidationStatus);
        command.Parameters.AddWithValue("expires_at", (object?)record.ExpiresAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgreSqlUnitysearchDecisionTraceStore(PostgreSqlConnectionFactory connections) : IUnitysearchDecisionTraceStore
{
    public async Task<Guid> WriteAsync(
        CompanyId companyId, UserId? userId, string context, string entityType,
        string? query, string? normalizedQuery, int? returnedCount, string traceJson,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO unitysearch_decision_traces (
                id, company_id, user_id, context, entity_type, query, normalized_query, returned_count, trace_json)
            VALUES (
                @id, @company_id, @user_id, @context, @entity_type, @query, @normalized_query, @returned_count, @trace_json::jsonb);
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("context", context);
        command.Parameters.AddWithValue("entity_type", entityType);
        command.Parameters.AddWithValue("query", (object?)query ?? DBNull.Value);
        command.Parameters.AddWithValue("normalized_query", (object?)normalizedQuery ?? DBNull.Value);
        command.Parameters.AddWithValue("returned_count", (object?)returnedCount ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("trace_json", NpgsqlDbType.Jsonb) { Value = traceJson });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }
}
