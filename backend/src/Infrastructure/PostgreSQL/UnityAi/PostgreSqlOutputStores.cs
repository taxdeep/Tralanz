using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.UnityAi;

public sealed class PostgreSqlReportUsageEventStore(PostgreSqlConnectionFactory connections) : IReportUsageEventStore
{
    public async Task RecordAsync(ReportUsageEventInput input, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO report_usage_events (
                company_id, user_id, report_key, event_type, date_range_key,
                filters_json, source_route, metadata_json)
            VALUES (
                @company_id, @user_id, @report_key, @event_type, @date_range_key,
                @filters_json::jsonb, @source_route, @metadata_json::jsonb);
            """;
        command.Parameters.AddWithValue("company_id", input.CompanyId);
        command.Parameters.AddWithValue("user_id", (object?)input.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("report_key", input.ReportKey);
        command.Parameters.AddWithValue("event_type", input.EventType);
        command.Parameters.AddWithValue("date_range_key", (object?)input.DateRangeKey ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("filters_json", NpgsqlDbType.Jsonb) { Value = (object?)input.FiltersJson ?? DBNull.Value });
        command.Parameters.AddWithValue("source_route", (object?)input.SourceRoute ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("metadata_json", NpgsqlDbType.Jsonb) { Value = (object?)input.MetadataJson ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgreSqlReportUsageStatStore(PostgreSqlConnectionFactory connections) : IReportUsageStatStore
{
    public async Task UpsertAsync(ReportUsageEventInput input, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Increment company-scope and (if user provided) user-scope rows.
        await UpsertOneAsync(connection, input, UnitysearchScopeType.Company, userId: null, occurredAt, cancellationToken).ConfigureAwait(false);
        if (input.UserId is not null)
        {
            await UpsertOneAsync(connection, input, UnitysearchScopeType.User, userId: input.UserId, occurredAt, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertOneAsync(
        NpgsqlConnection connection,
        ReportUsageEventInput input, string scopeType, UserId? userId,
        DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        // Map event type to which counter to bump.
        var openIncrement = input.EventType == ReportUsageEventType.ReportOpened ? 1 : 0;
        var exportIncrement = input.EventType == ReportUsageEventType.ReportExported ? 1 : 0;
        var printIncrement = input.EventType == ReportUsageEventType.ReportPrinted ? 1 : 0;
        var drilldownIncrement = input.EventType == ReportUsageEventType.ReportDrilldownClicked ? 1 : 0;
        var filterIncrement = input.EventType == ReportUsageEventType.ReportFiltered ? 1 : 0;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO report_usage_stats (
                company_id, scope_type, user_id, report_key,
                open_count, export_count, print_count, drilldown_count, filter_count,
                last_opened_at, last_used_at, common_date_range_key, updated_at)
            VALUES (
                @company_id, @scope_type, @user_id, @report_key,
                @open_inc, @export_inc, @print_inc, @drilldown_inc, @filter_inc,
                CASE WHEN @open_inc > 0 THEN @occurred_at ELSE NULL END,
                @occurred_at,
                @date_range_key,
                @occurred_at)
            ON CONFLICT (company_id, scope_type, COALESCE(user_id, '00000000-0000-0000-0000-000000000000'), report_key)
            DO UPDATE SET
                open_count = report_usage_stats.open_count + EXCLUDED.open_count,
                export_count = report_usage_stats.export_count + EXCLUDED.export_count,
                print_count = report_usage_stats.print_count + EXCLUDED.print_count,
                drilldown_count = report_usage_stats.drilldown_count + EXCLUDED.drilldown_count,
                filter_count = report_usage_stats.filter_count + EXCLUDED.filter_count,
                last_opened_at = COALESCE(EXCLUDED.last_opened_at, report_usage_stats.last_opened_at),
                last_used_at = EXCLUDED.last_used_at,
                common_date_range_key = COALESCE(EXCLUDED.common_date_range_key, report_usage_stats.common_date_range_key),
                updated_at = EXCLUDED.updated_at;
            """;
        command.Parameters.AddWithValue("company_id", input.CompanyId);
        command.Parameters.AddWithValue("scope_type", scopeType);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("report_key", input.ReportKey);
        command.Parameters.AddWithValue("open_inc", openIncrement);
        command.Parameters.AddWithValue("export_inc", exportIncrement);
        command.Parameters.AddWithValue("print_inc", printIncrement);
        command.Parameters.AddWithValue("drilldown_inc", drilldownIncrement);
        command.Parameters.AddWithValue("filter_inc", filterIncrement);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
        command.Parameters.AddWithValue("date_range_key", (object?)input.DateRangeKey ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReportUsageStatRecord>> GetForCompanyAsync(
        CompanyId companyId, UserId? userId, string scopeType, CancellationToken cancellationToken)
    {
        var items = new List<ReportUsageStatRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, company_id, scope_type, user_id, report_key,
                   open_count, export_count, print_count, drilldown_count, filter_count,
                   last_opened_at, last_used_at, common_date_range_key, updated_at
            FROM report_usage_stats
            WHERE company_id = @company_id
              AND scope_type = @scope_type
              AND COALESCE(user_id, '00000000-0000-0000-0000-000000000000') = COALESCE(@user_id, '00000000-0000-0000-0000-000000000000');
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("scope_type", scopeType);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new ReportUsageStatRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.GetGuid(1),
                ScopeType: reader.GetString(2),
                UserId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                ReportKey: reader.GetString(4),
                OpenCount: reader.GetInt32(5),
                ExportCount: reader.GetInt32(6),
                PrintCount: reader.GetInt32(7),
                DrilldownCount: reader.GetInt32(8),
                FilterCount: reader.GetInt32(9),
                LastOpenedAt: reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                LastUsedAt: reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                CommonDateRangeKey: reader.IsDBNull(12) ? null : reader.GetString(12),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(13)));
        }
        return items;
    }
}

public sealed class PostgreSqlDashboardUserWidgetStore(PostgreSqlConnectionFactory connections) : IDashboardUserWidgetStore
{
    public async Task<IReadOnlyList<DashboardUserWidgetRecord>> GetActiveAsync(
        CompanyId companyId, UserId? userId, CancellationToken cancellationToken)
    {
        var items = new List<DashboardUserWidgetRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, company_id, user_id, widget_key, title, config_json::text,
                   position, source, active, created_at, updated_at
            FROM dashboard_user_widgets
            WHERE company_id = @company_id
              AND COALESCE(user_id, '00000000-0000-0000-0000-000000000000') = COALESCE(@user_id, '00000000-0000-0000-0000-000000000000')
              AND active = TRUE
            ORDER BY position NULLS LAST, created_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new DashboardUserWidgetRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.GetGuid(1),
                UserId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                WidgetKey: reader.GetString(3),
                Title: reader.IsDBNull(4) ? null : reader.GetString(4),
                ConfigJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                Position: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Source: reader.GetString(7),
                Active: reader.GetBoolean(8),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(9),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(10)));
        }
        return items;
    }

    public async Task UpsertAsync(DashboardUserWidgetRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dashboard_user_widgets (
                id, company_id, user_id, widget_key, title, config_json, position, source, active,
                created_at, updated_at)
            VALUES (
                @id, @company_id, @user_id, @widget_key, @title, @config_json::jsonb,
                @position, @source, @active, @created_at, @updated_at)
            ON CONFLICT (company_id, COALESCE(user_id, '00000000-0000-0000-0000-000000000000'), widget_key)
            DO UPDATE SET
                title = EXCLUDED.title,
                config_json = EXCLUDED.config_json,
                position = EXCLUDED.position,
                active = EXCLUDED.active,
                updated_at = EXCLUDED.updated_at;
            """;
        command.Parameters.AddWithValue("id", record.Id == Guid.Empty ? Guid.NewGuid() : record.Id);
        command.Parameters.AddWithValue("company_id", record.CompanyId);
        command.Parameters.AddWithValue("user_id", (object?)record.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("widget_key", record.WidgetKey);
        command.Parameters.AddWithValue("title", (object?)record.Title ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("config_json", NpgsqlDbType.Jsonb) { Value = (object?)record.ConfigJson ?? DBNull.Value });
        command.Parameters.AddWithValue("position", (object?)record.Position ?? DBNull.Value);
        command.Parameters.AddWithValue("source", record.Source);
        command.Parameters.AddWithValue("active", record.Active);
        command.Parameters.AddWithValue("created_at", record.CreatedAt);
        command.Parameters.AddWithValue("updated_at", record.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgreSqlDashboardWidgetSuggestionStore(PostgreSqlConnectionFactory connections) : IDashboardWidgetSuggestionStore
{
    public async Task<DashboardWidgetSuggestionRecord?> GetByIdAsync(CompanyId companyId, Guid suggestionId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE company_id = @company_id AND id = @id LIMIT 1;";
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", suggestionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<DashboardWidgetSuggestionRecord>> GetForUserAsync(
        CompanyId companyId, UserId? userId, string? statusFilter, CancellationToken cancellationToken)
    {
        var items = new List<DashboardWidgetSuggestionRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = "company_id = @company_id AND COALESCE(user_id, '00000000-0000-0000-0000-000000000000') = COALESCE(@user_id, '00000000-0000-0000-0000-000000000000')";
        if (statusFilter is not null) where += " AND status = @status";
        command.CommandText = SelectColumns + " WHERE " + where + " ORDER BY created_at DESC;";
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);
        if (statusFilter is not null) command.Parameters.AddWithValue("status", statusFilter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Map(reader));
        }
        return items;
    }

    public async Task<IReadOnlyList<DashboardWidgetSuggestionRecord>> GetExistingForWidgetKeysAsync(
        CompanyId companyId, UserId? userId, IReadOnlyCollection<string> widgetKeys, CancellationToken cancellationToken)
    {
        if (widgetKeys.Count == 0)
        {
            return Array.Empty<DashboardWidgetSuggestionRecord>();
        }

        var items = new List<DashboardWidgetSuggestionRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns +
            " WHERE company_id = @company_id" +
            " AND COALESCE(user_id, '00000000-0000-0000-0000-000000000000') = COALESCE(@user_id, '00000000-0000-0000-0000-000000000000')" +
            " AND widget_key = ANY(@widget_keys);";
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("widget_keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = widgetKeys.ToArray() });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Map(reader));
        }
        return items;
    }

    public async Task<Guid> InsertAsync(DashboardWidgetSuggestionRecord record, CancellationToken cancellationToken)
    {
        var id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dashboard_widget_suggestions (
                id, company_id, user_id, widget_key, title, reason, evidence_json,
                confidence, source, status, job_run_id,
                accepted_at, dismissed_at, snoozed_until, created_at, updated_at)
            VALUES (
                @id, @company_id, @user_id, @widget_key, @title, @reason, @evidence_json::jsonb,
                @confidence, @source, @status, @job_run_id,
                @accepted_at, @dismissed_at, @snoozed_until, @created_at, @updated_at);
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company_id", record.CompanyId);
        command.Parameters.AddWithValue("user_id", (object?)record.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("widget_key", record.WidgetKey);
        command.Parameters.AddWithValue("title", record.Title);
        command.Parameters.AddWithValue("reason", record.Reason);
        command.Parameters.Add(new NpgsqlParameter("evidence_json", NpgsqlDbType.Jsonb) { Value = (object?)record.EvidenceJson ?? DBNull.Value });
        command.Parameters.AddWithValue("confidence", record.Confidence);
        command.Parameters.AddWithValue("source", record.Source);
        command.Parameters.AddWithValue("status", record.Status);
        command.Parameters.AddWithValue("job_run_id", (object?)record.JobRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("accepted_at", (object?)record.AcceptedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("dismissed_at", (object?)record.DismissedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("snoozed_until", (object?)record.SnoozedUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", record.CreatedAt);
        command.Parameters.AddWithValue("updated_at", record.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task UpdateStatusAsync(
        Guid suggestionId, string status,
        DateTimeOffset? acceptedAt, DateTimeOffset? dismissedAt, DateTimeOffset? snoozedUntil,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dashboard_widget_suggestions
               SET status = @status,
                   accepted_at = @accepted_at,
                   dismissed_at = @dismissed_at,
                   snoozed_until = @snoozed_until,
                   updated_at = @updated_at
             WHERE id = @id;
            """;
        command.Parameters.AddWithValue("id", suggestionId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("accepted_at", (object?)acceptedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("dismissed_at", (object?)dismissedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("snoozed_until", (object?)snoozedUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SelectColumns = """
        SELECT id, company_id, user_id, widget_key, title, reason, evidence_json::text,
               confidence, source, status, job_run_id,
               accepted_at, dismissed_at, snoozed_until, created_at, updated_at
        FROM dashboard_widget_suggestions
        """;

    private static DashboardWidgetSuggestionRecord Map(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: reader.GetGuid(1),
        UserId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
        WidgetKey: reader.GetString(3),
        Title: reader.GetString(4),
        Reason: reader.GetString(5),
        EvidenceJson: reader.IsDBNull(6) ? null : reader.GetString(6),
        Confidence: reader.GetDecimal(7),
        Source: reader.GetString(8),
        Status: reader.GetString(9),
        JobRunId: reader.IsDBNull(10) ? null : reader.GetGuid(10),
        AcceptedAt: reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
        DismissedAt: reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
        SnoozedUntil: reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(14),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(15));
}

public sealed class PostgreSqlActionCenterTaskStore(PostgreSqlConnectionFactory connections) : IActionCenterTaskStore
{
    public async Task<ActionCenterTaskRecord?> GetByIdAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE company_id = @company_id AND id = @id LIMIT 1;";
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", taskId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<ActionCenterTaskRecord?> GetByFingerprintAsync(CompanyId companyId, string fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE company_id = @company_id AND fingerprint = @fingerprint LIMIT 1;";
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<ActionCenterTaskRecord>> GetTasksAsync(
        CompanyId companyId, UserId? assignedUserId,
        IReadOnlyCollection<string>? statuses, CancellationToken cancellationToken)
    {
        var items = new List<ActionCenterTaskRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = new List<string> { "company_id = @company_id" };
        if (assignedUserId is not null) where.Add("(assigned_user_id = @user_id OR assigned_user_id IS NULL)");
        if (statuses is not null && statuses.Count > 0) where.Add("status = ANY(@statuses)");
        command.CommandText = SelectColumns + " WHERE " + string.Join(" AND ", where) + " ORDER BY priority, due_date NULLS LAST, created_at DESC;";
        command.Parameters.AddWithValue("company_id", companyId);
        if (assignedUserId is not null) command.Parameters.AddWithValue("user_id", assignedUserId);
        if (statuses is not null && statuses.Count > 0)
            command.Parameters.Add(new NpgsqlParameter("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = statuses.ToArray() });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Map(reader));
        }
        return items;
    }

    public async Task<Guid> InsertAsync(ActionCenterTaskRecord record, CancellationToken cancellationToken)
    {
        var id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO action_center_tasks (
                id, company_id, assigned_user_id, task_type, source_engine, source_type, source_object_id,
                title, description, reason, evidence_json, priority, due_date, action_url,
                status, fingerprint, ai_generated, confidence, created_at, updated_at,
                completed_at, dismissed_at, snoozed_until)
            VALUES (
                @id, @company_id, @assigned_user_id, @task_type, @source_engine, @source_type, @source_object_id,
                @title, @description, @reason, @evidence_json::jsonb, @priority, @due_date, @action_url,
                @status, @fingerprint, @ai_generated, @confidence, @created_at, @updated_at,
                @completed_at, @dismissed_at, @snoozed_until);
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company_id", record.CompanyId);
        command.Parameters.AddWithValue("assigned_user_id", (object?)record.AssignedUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("task_type", record.TaskType);
        command.Parameters.AddWithValue("source_engine", record.SourceEngine);
        command.Parameters.AddWithValue("source_type", record.SourceType);
        command.Parameters.AddWithValue("source_object_id", (object?)record.SourceObjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("title", record.Title);
        command.Parameters.AddWithValue("description", (object?)record.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("reason", record.Reason);
        command.Parameters.Add(new NpgsqlParameter("evidence_json", NpgsqlDbType.Jsonb) { Value = (object?)record.EvidenceJson ?? DBNull.Value });
        command.Parameters.AddWithValue("priority", record.Priority);
        command.Parameters.AddWithValue("due_date", (object?)record.DueDate ?? DBNull.Value);
        command.Parameters.AddWithValue("action_url", (object?)record.ActionUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("status", record.Status);
        command.Parameters.AddWithValue("fingerprint", record.Fingerprint);
        command.Parameters.AddWithValue("ai_generated", record.AiGenerated);
        command.Parameters.AddWithValue("confidence", (object?)record.Confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", record.CreatedAt);
        command.Parameters.AddWithValue("updated_at", record.UpdatedAt);
        command.Parameters.AddWithValue("completed_at", (object?)record.CompletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("dismissed_at", (object?)record.DismissedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("snoozed_until", (object?)record.SnoozedUntil ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task UpdateStatusAsync(
        CompanyId companyId, Guid taskId, string status,
        DateTimeOffset? completedAt, DateTimeOffset? dismissedAt, DateTimeOffset? snoozedUntil,
        DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE action_center_tasks
               SET status = @status,
                   completed_at = @completed_at,
                   dismissed_at = @dismissed_at,
                   snoozed_until = @snoozed_until,
                   updated_at = @updated_at
             WHERE company_id = @company_id AND id = @id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", taskId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("completed_at", (object?)completedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("dismissed_at", (object?)dismissedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("snoozed_until", (object?)snoozedUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_at", updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SelectColumns = """
        SELECT id, company_id, assigned_user_id, task_type, source_engine, source_type, source_object_id,
               title, description, reason, evidence_json::text, priority, due_date, action_url,
               status, fingerprint, ai_generated, confidence, created_at, updated_at,
               completed_at, dismissed_at, snoozed_until
        FROM action_center_tasks
        """;

    private static ActionCenterTaskRecord Map(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: reader.GetGuid(1),
        AssignedUserId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
        TaskType: reader.GetString(3),
        SourceEngine: reader.GetString(4),
        SourceType: reader.GetString(5),
        SourceObjectId: reader.IsDBNull(6) ? null : reader.GetGuid(6),
        Title: reader.GetString(7),
        Description: reader.IsDBNull(8) ? null : reader.GetString(8),
        Reason: reader.GetString(9),
        EvidenceJson: reader.IsDBNull(10) ? null : reader.GetString(10),
        Priority: reader.GetString(11),
        DueDate: reader.IsDBNull(12) ? null : reader.GetFieldValue<DateOnly>(12),
        ActionUrl: reader.IsDBNull(13) ? null : reader.GetString(13),
        Status: reader.GetString(14),
        Fingerprint: reader.GetString(15),
        AiGenerated: reader.GetBoolean(16),
        Confidence: reader.IsDBNull(17) ? null : reader.GetDecimal(17),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(18),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(19),
        CompletedAt: reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20),
        DismissedAt: reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21),
        SnoozedUntil: reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22));
}

public sealed class PostgreSqlActionCenterTaskEventStore(PostgreSqlConnectionFactory connections) : IActionCenterTaskEventStore
{
    public async Task RecordAsync(
        CompanyId companyId, Guid taskId, UserId? userId, string eventType, string? metadataJson,
        DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO action_center_task_events (company_id, task_id, user_id, event_type, metadata_json, created_at)
            VALUES (@company_id, @task_id, @user_id, @event_type, @metadata_json::jsonb, @created_at);
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("task_id", taskId);
        command.Parameters.AddWithValue("user_id", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.Add(new NpgsqlParameter("metadata_json", NpgsqlDbType.Jsonb) { Value = (object?)metadataJson ?? DBNull.Value });
        command.Parameters.AddWithValue("created_at", occurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
