using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.UnityAi;

public sealed class PostgreSqlAiJobRunStore(PostgreSqlConnectionFactory connections) : IAiJobRunStore
{
    public async Task<Guid> StartAsync(
        Guid? companyId, string jobType, string triggerType,
        Guid? triggeredByUserId,
        DateTimeOffset? sourceWindowStart, DateTimeOffset? sourceWindowEnd,
        string? inputSummaryJson,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_job_runs (
                id, company_id, job_type, status, trigger_type, triggered_by_user_id,
                started_at, source_window_start, source_window_end, input_summary_json,
                created_at, updated_at)
            VALUES (
                @id, @company_id, @job_type, @status, @trigger_type, @triggered_by_user_id,
                @started_at, @source_window_start, @source_window_end, @input_summary_json::jsonb,
                @created_at, @updated_at);
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company_id", (object?)companyId ?? DBNull.Value);
        command.Parameters.AddWithValue("job_type", jobType);
        command.Parameters.AddWithValue("status", AiJobRunStatus.Running);
        command.Parameters.AddWithValue("trigger_type", triggerType);
        command.Parameters.AddWithValue("triggered_by_user_id", (object?)triggeredByUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("started_at", now);
        command.Parameters.AddWithValue("source_window_start", (object?)sourceWindowStart ?? DBNull.Value);
        command.Parameters.AddWithValue("source_window_end", (object?)sourceWindowEnd ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("input_summary_json", NpgsqlDbType.Jsonb) { Value = (object?)inputSummaryJson ?? DBNull.Value });
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("updated_at", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task CompleteAsync(
        Guid jobRunId, string status,
        string? outputSummaryJson, string? errorMessage, string? warningsJson,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_job_runs
               SET status = @status,
                   finished_at = @finished_at,
                   output_summary_json = @output_summary_json::jsonb,
                   error_message = @error_message,
                   warnings_json = @warnings_json::jsonb,
                   updated_at = @updated_at
             WHERE id = @id;
            """;
        command.Parameters.AddWithValue("id", jobRunId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("finished_at", now);
        command.Parameters.Add(new NpgsqlParameter("output_summary_json", NpgsqlDbType.Jsonb) { Value = (object?)outputSummaryJson ?? DBNull.Value });
        command.Parameters.AddWithValue("error_message", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("warnings_json", NpgsqlDbType.Jsonb) { Value = (object?)warningsJson ?? DBNull.Value });
        command.Parameters.AddWithValue("updated_at", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AiJobRunRecord>> GetRecentAsync(
        CompanyId companyId, string? jobType, int limit, CancellationToken cancellationToken)
    {
        var items = new List<AiJobRunRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = jobType is null
            ? """
              SELECT id, company_id, job_type, status, trigger_type, triggered_by_user_id,
                     started_at, finished_at, source_window_start, source_window_end,
                     input_summary_json::text, output_summary_json::text,
                     error_message, warnings_json::text, created_at, updated_at
              FROM ai_job_runs WHERE company_id = @company_id ORDER BY created_at DESC LIMIT @limit;
              """
            : """
              SELECT id, company_id, job_type, status, trigger_type, triggered_by_user_id,
                     started_at, finished_at, source_window_start, source_window_end,
                     input_summary_json::text, output_summary_json::text,
                     error_message, warnings_json::text, created_at, updated_at
              FROM ai_job_runs WHERE company_id = @company_id AND job_type = @job_type
              ORDER BY created_at DESC LIMIT @limit;
              """;
        command.Parameters.AddWithValue("company_id", companyId);
        if (jobType is not null) command.Parameters.AddWithValue("job_type", jobType);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new AiJobRunRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
                JobType: reader.GetString(2),
                Status: reader.GetString(3),
                TriggerType: reader.GetString(4),
                TriggeredByUserId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
                StartedAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                FinishedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                SourceWindowStart: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                SourceWindowEnd: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                InputSummaryJson: reader.IsDBNull(10) ? null : reader.GetString(10),
                OutputSummaryJson: reader.IsDBNull(11) ? null : reader.GetString(11),
                ErrorMessage: reader.IsDBNull(12) ? null : reader.GetString(12),
                WarningsJson: reader.IsDBNull(13) ? null : reader.GetString(13),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(14),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(15)));
        }
        return items;
    }

    public async Task<IReadOnlyList<AiJobRunRecord>> GetRecentPlatformAsync(
        int limit, CancellationToken cancellationToken)
    {
        var items = new List<AiJobRunRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, company_id, job_type, status, trigger_type, triggered_by_user_id,
                   started_at, finished_at, source_window_start, source_window_end,
                   input_summary_json::text, output_summary_json::text,
                   error_message, warnings_json::text, created_at, updated_at
              FROM ai_job_runs
             ORDER BY created_at DESC
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new AiJobRunRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
                JobType: reader.GetString(2),
                Status: reader.GetString(3),
                TriggerType: reader.GetString(4),
                TriggeredByUserId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
                StartedAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                FinishedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                SourceWindowStart: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                SourceWindowEnd: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                InputSummaryJson: reader.IsDBNull(10) ? null : reader.GetString(10),
                OutputSummaryJson: reader.IsDBNull(11) ? null : reader.GetString(11),
                ErrorMessage: reader.IsDBNull(12) ? null : reader.GetString(12),
                WarningsJson: reader.IsDBNull(13) ? null : reader.GetString(13),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(14),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(15)));
        }
        return items;
    }
}

public sealed class PostgreSqlAiRequestLogStore(PostgreSqlConnectionFactory connections) : IAiRequestLogStore
{
    public async Task<Guid> WriteAsync(AiRequestLogRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_request_logs (
                id, company_id, job_run_id, task_type, provider, model,
                request_schema_version, response_schema_version,
                input_hash, input_redacted_json, output_redacted_json,
                status, error_message, prompt_version,
                token_input_count, token_output_count, estimated_cost, latency_ms,
                created_at)
            VALUES (
                @id, @company_id, @job_run_id, @task_type, @provider, @model,
                @request_schema_version, @response_schema_version,
                @input_hash, @input_redacted_json::jsonb, @output_redacted_json::jsonb,
                @status, @error_message, @prompt_version,
                @token_input_count, @token_output_count, @estimated_cost, @latency_ms,
                @created_at);
            """;
        command.Parameters.AddWithValue("id", record.Id);
        command.Parameters.AddWithValue("company_id", (object?)record.CompanyId ?? DBNull.Value);
        command.Parameters.AddWithValue("job_run_id", (object?)record.JobRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("task_type", record.TaskType);
        command.Parameters.AddWithValue("provider", (object?)record.Provider ?? DBNull.Value);
        command.Parameters.AddWithValue("model", (object?)record.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("request_schema_version", (object?)record.RequestSchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("response_schema_version", (object?)record.ResponseSchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("input_hash", (object?)record.InputHash ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("input_redacted_json", NpgsqlDbType.Jsonb) { Value = (object?)record.InputRedactedJson ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("output_redacted_json", NpgsqlDbType.Jsonb) { Value = (object?)record.OutputRedactedJson ?? DBNull.Value });
        command.Parameters.AddWithValue("status", record.Status);
        command.Parameters.AddWithValue("error_message", (object?)record.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("prompt_version", (object?)record.PromptVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("token_input_count", (object?)record.TokenInputCount ?? DBNull.Value);
        command.Parameters.AddWithValue("token_output_count", (object?)record.TokenOutputCount ?? DBNull.Value);
        command.Parameters.AddWithValue("estimated_cost", (object?)record.EstimatedCost ?? DBNull.Value);
        command.Parameters.AddWithValue("latency_ms", (object?)record.LatencyMs ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", record.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record.Id;
    }

    public async Task<IReadOnlyList<AiRequestLogRecord>> GetRecentAsync(
        CompanyId companyId, string? taskType, int limit, CancellationToken cancellationToken)
    {
        var items = new List<AiRequestLogRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = taskType is null
            ? """
              SELECT id, company_id, job_run_id, task_type, provider, model,
                     request_schema_version, response_schema_version, input_hash,
                     input_redacted_json::text, output_redacted_json::text,
                     status, error_message, prompt_version,
                     token_input_count, token_output_count, estimated_cost, latency_ms, created_at
              FROM ai_request_logs WHERE company_id = @company_id ORDER BY created_at DESC LIMIT @limit;
              """
            : """
              SELECT id, company_id, job_run_id, task_type, provider, model,
                     request_schema_version, response_schema_version, input_hash,
                     input_redacted_json::text, output_redacted_json::text,
                     status, error_message, prompt_version,
                     token_input_count, token_output_count, estimated_cost, latency_ms, created_at
              FROM ai_request_logs WHERE company_id = @company_id AND task_type = @task_type
              ORDER BY created_at DESC LIMIT @limit;
              """;
        command.Parameters.AddWithValue("company_id", companyId);
        if (taskType is not null) command.Parameters.AddWithValue("task_type", taskType);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new AiRequestLogRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
                JobRunId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                TaskType: reader.GetString(3),
                Provider: reader.IsDBNull(4) ? null : reader.GetString(4),
                Model: reader.IsDBNull(5) ? null : reader.GetString(5),
                RequestSchemaVersion: reader.IsDBNull(6) ? null : reader.GetString(6),
                ResponseSchemaVersion: reader.IsDBNull(7) ? null : reader.GetString(7),
                InputHash: reader.IsDBNull(8) ? null : reader.GetString(8),
                InputRedactedJson: reader.IsDBNull(9) ? null : reader.GetString(9),
                OutputRedactedJson: reader.IsDBNull(10) ? null : reader.GetString(10),
                Status: reader.GetString(11),
                ErrorMessage: reader.IsDBNull(12) ? null : reader.GetString(12),
                PromptVersion: reader.IsDBNull(13) ? null : reader.GetString(13),
                TokenInputCount: reader.IsDBNull(14) ? null : reader.GetInt32(14),
                TokenOutputCount: reader.IsDBNull(15) ? null : reader.GetInt32(15),
                EstimatedCost: reader.IsDBNull(16) ? null : reader.GetDecimal(16),
                LatencyMs: reader.IsDBNull(17) ? null : reader.GetInt32(17),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(18)));
        }
        return items;
    }

    public async Task<IReadOnlyList<AiRequestLogRecord>> GetRecentPlatformAsync(
        int limit, CancellationToken cancellationToken)
    {
        var items = new List<AiRequestLogRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, company_id, job_run_id, task_type, provider, model,
                   request_schema_version, response_schema_version, input_hash,
                   input_redacted_json::text, output_redacted_json::text,
                   status, error_message, prompt_version,
                   token_input_count, token_output_count, estimated_cost, latency_ms, created_at
              FROM ai_request_logs
             ORDER BY created_at DESC
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new AiRequestLogRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
                JobRunId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                TaskType: reader.GetString(3),
                Provider: reader.IsDBNull(4) ? null : reader.GetString(4),
                Model: reader.IsDBNull(5) ? null : reader.GetString(5),
                RequestSchemaVersion: reader.IsDBNull(6) ? null : reader.GetString(6),
                ResponseSchemaVersion: reader.IsDBNull(7) ? null : reader.GetString(7),
                InputHash: reader.IsDBNull(8) ? null : reader.GetString(8),
                InputRedactedJson: reader.IsDBNull(9) ? null : reader.GetString(9),
                OutputRedactedJson: reader.IsDBNull(10) ? null : reader.GetString(10),
                Status: reader.GetString(11),
                ErrorMessage: reader.IsDBNull(12) ? null : reader.GetString(12),
                PromptVersion: reader.IsDBNull(13) ? null : reader.GetString(13),
                TokenInputCount: reader.IsDBNull(14) ? null : reader.GetInt32(14),
                TokenOutputCount: reader.IsDBNull(15) ? null : reader.GetInt32(15),
                EstimatedCost: reader.IsDBNull(16) ? null : reader.GetDecimal(16),
                LatencyMs: reader.IsDBNull(17) ? null : reader.GetInt32(17),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(18)));
        }
        return items;
    }

    public async Task<AiActivitySummary> GetPlatformSummaryAsync(
        DateTimeOffset windowStart, CancellationToken cancellationToken)
    {
        // Single roll-up query: counts, token sums, cost sum, avg
        // latency, last call timestamp. AiRequestLogStatus.Succeeded
        // is the canonical OK status; everything else (failed,
        // invalid_output, …) counts as a failure for the success-rate
        // ratio.
        const string sql = """
            SELECT
              count(*)::int AS total_calls,
              count(*) filter (where status = 'succeeded')::int AS succeeded_calls,
              count(*) filter (where status <> 'succeeded')::int AS failed_calls,
              coalesce(sum(token_input_count), 0)::bigint AS total_input_tokens,
              coalesce(sum(token_output_count), 0)::bigint AS total_output_tokens,
              coalesce(sum(estimated_cost), 0)::numeric(20,6) AS total_estimated_cost,
              avg(latency_ms)::int AS avg_latency_ms,
              max(created_at) AS last_call_at
              FROM ai_request_logs
             WHERE created_at >= @window_start;
            """;

        var windowEnd = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("window_start", windowStart);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new AiActivitySummary(
            WindowStart: windowStart,
            WindowEnd: windowEnd,
            TotalCalls: reader.GetInt32(0),
            SucceededCalls: reader.GetInt32(1),
            FailedCalls: reader.GetInt32(2),
            TotalInputTokens: reader.GetInt64(3),
            TotalOutputTokens: reader.GetInt64(4),
            TotalEstimatedCost: reader.GetDecimal(5),
            AvgLatencyMs: reader.IsDBNull(6) ? null : reader.GetInt32(6),
            LastCallAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
    }
}
