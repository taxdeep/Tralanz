using System.Diagnostics;
using System.Globalization;
using Citus.Platform.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

/// <summary>
/// Postgres-backed implementation of <see cref="IPlatformDatabaseAdminService"/>.
/// Uses the same <c>PlatformPostgresConnectionFactory</c> connection
/// string for VACUUM / table-size queries and parses it with
/// <c>NpgsqlConnectionStringBuilder</c> to feed pg_dump's command-line
/// arguments + PGPASSWORD env var.
/// </summary>
public sealed class PostgresPlatformDatabaseAdminService : IPlatformDatabaseAdminService
{
    private readonly PlatformPostgresConnectionFactory _connections;
    private readonly string _connectionString;
    private readonly PlatformDatabaseAdminOptions _options;
    private readonly ILogger<PostgresPlatformDatabaseAdminService> _logger;

    public PostgresPlatformDatabaseAdminService(
        PlatformPostgresConnectionFactory connections,
        string connectionString,
        IOptions<PlatformDatabaseAdminOptions> options,
        ILogger<PostgresPlatformDatabaseAdminService> logger)
    {
        _connections = connections;
        _connectionString = connectionString;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists platform_database_backups (
              id uuid primary key default gen_random_uuid(),
              started_at timestamptz not null default now(),
              completed_at timestamptz,
              status text not null,
              file_path text,
              size_bytes bigint,
              triggered_by_user_id uuid not null,
              error_message text,
              constraint platform_database_backups_status_chk
                check (status in ('running','succeeded','failed'))
            );

            create index if not exists ix_platform_database_backups_started_at
              on platform_database_backups (started_at desc);

            create table if not exists platform_database_maintenance_runs (
              id uuid primary key default gen_random_uuid(),
              operation text not null,
              started_at timestamptz not null default now(),
              completed_at timestamptz,
              status text not null,
              duration_ms bigint,
              triggered_by_user_id uuid not null,
              error_message text,
              constraint platform_database_maintenance_runs_status_chk
                check (status in ('running','succeeded','failed'))
            );

            create index if not exists ix_platform_database_maintenance_runs_started_at
              on platform_database_maintenance_runs (started_at desc);
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlatformDatabaseBackupRecord> StartBackupAsync(
        UserId triggeredByUserId,
        CancellationToken cancellationToken)
    {
        var connectionInfo = ParseConnection();
        var rootPath = _options.BackupRootPath;
        Directory.CreateDirectory(rootPath);

        var fileName = $"backup_{connectionInfo.Database}_{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}_{Guid.NewGuid():N}.sql";
        var filePath = Path.Combine(rootPath, fileName);

        const string insertSql = """
            insert into platform_database_backups
              (started_at, status, file_path, triggered_by_user_id)
            values
              (now(), 'running', @file_path, @triggered_by_user_id)
            returning id, started_at;
            """;

        Guid backupId;
        DateTimeOffset startedAt;
        await using (var connection = await _connections.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = insertSql;
            command.Parameters.AddWithValue("file_path", filePath);
            command.Parameters.AddWithValue("triggered_by_user_id", triggeredByUserId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            backupId = reader.GetGuid(0);
            startedAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        // Fire-and-forget: pg_dump can take minutes on a moderately
        // sized database. Log + persist the outcome on the spawned
        // task; the SysAdmin UI polls ListBackupsAsync to render
        // status transitions.
        _ = Task.Run(() => RunPgDumpAsync(backupId, filePath, connectionInfo), CancellationToken.None);

        return new PlatformDatabaseBackupRecord(
            Id: backupId,
            StartedAt: startedAt,
            CompletedAt: null,
            Status: PlatformDatabaseStatuses.Running,
            FilePath: filePath,
            SizeBytes: null,
            TriggeredByUserId: triggeredByUserId,
            ErrorMessage: null);
    }

    public async Task<IReadOnlyList<PlatformDatabaseBackupRecord>> ListBackupsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(limit, 1, 200);
        const string sql = """
            select id, started_at, completed_at, status, file_path,
                   size_bytes, triggered_by_user_id, error_message
              from platform_database_backups
             order by started_at desc
             limit @limit;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("limit", clamped);

        var results = new List<PlatformDatabaseBackupRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PlatformDatabaseBackupRecord(
                Id: reader.GetGuid(0),
                StartedAt: reader.GetFieldValue<DateTimeOffset>(1),
                CompletedAt: reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                Status: reader.GetString(3),
                FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
                SizeBytes: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                TriggeredByUserId: reader.GetGuid(6),
                ErrorMessage: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }

    public async Task<PlatformDatabaseBackupRecord?> GetBackupAsync(
        Guid backupId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, started_at, completed_at, status, file_path,
                   size_bytes, triggered_by_user_id, error_message
              from platform_database_backups
             where id = @id;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", backupId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PlatformDatabaseBackupRecord(
            Id: reader.GetGuid(0),
            StartedAt: reader.GetFieldValue<DateTimeOffset>(1),
            CompletedAt: reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            Status: reader.GetString(3),
            FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
            SizeBytes: reader.IsDBNull(5) ? null : reader.GetInt64(5),
            TriggeredByUserId: reader.GetGuid(6),
            ErrorMessage: reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public async Task<PlatformDatabaseMaintenanceRun> RunVacuumAnalyzeAsync(
        UserId triggeredByUserId,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
            insert into platform_database_maintenance_runs
              (operation, started_at, status, triggered_by_user_id)
            values
              (@operation, now(), 'running', @triggered_by_user_id)
            returning id, started_at;
            """;

        Guid runId;
        DateTimeOffset startedAt;
        await using (var connection = await _connections.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = insertSql;
            command.Parameters.AddWithValue("operation", PlatformDatabaseOperations.VacuumAnalyze);
            command.Parameters.AddWithValue("triggered_by_user_id", triggeredByUserId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            runId = reader.GetGuid(0);
            startedAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        var stopwatch = Stopwatch.StartNew();
        string status;
        string? errorMessage = null;
        DateTimeOffset completedAt;
        long durationMs;

        try
        {
            // VACUUM cannot run inside a transaction — open a fresh
            // connection in autocommit and execute the full statement.
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM (ANALYZE);";
            command.CommandTimeout = 0; // No timeout — VACUUM yields internally.
            await command.ExecuteNonQueryAsync(cancellationToken);
            status = PlatformDatabaseStatuses.Succeeded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VACUUM ANALYZE failed (run {RunId}).", runId);
            status = PlatformDatabaseStatuses.Failed;
            errorMessage = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            completedAt = DateTimeOffset.UtcNow;
            durationMs = (long)stopwatch.Elapsed.TotalMilliseconds;
        }

        const string updateSql = """
            update platform_database_maintenance_runs
               set completed_at = @completed_at,
                   status = @status,
                   duration_ms = @duration_ms,
                   error_message = @error_message
             where id = @id;
            """;

        await using (var connection = await _connections.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = updateSql;
            command.Parameters.AddWithValue("id", runId);
            command.Parameters.AddWithValue("completed_at", completedAt);
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("duration_ms", durationMs);
            command.Parameters.AddWithValue("error_message", (object?)errorMessage ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new PlatformDatabaseMaintenanceRun(
            Id: runId,
            Operation: PlatformDatabaseOperations.VacuumAnalyze,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            Status: status,
            DurationMs: durationMs,
            TriggeredByUserId: triggeredByUserId,
            ErrorMessage: errorMessage);
    }

    public async Task<IReadOnlyList<PlatformDatabaseMaintenanceRun>> ListMaintenanceRunsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(limit, 1, 200);
        const string sql = """
            select id, operation, started_at, completed_at, status,
                   duration_ms, triggered_by_user_id, error_message
              from platform_database_maintenance_runs
             order by started_at desc
             limit @limit;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("limit", clamped);

        var results = new List<PlatformDatabaseMaintenanceRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PlatformDatabaseMaintenanceRun(
                Id: reader.GetGuid(0),
                Operation: reader.GetString(1),
                StartedAt: reader.GetFieldValue<DateTimeOffset>(2),
                CompletedAt: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                Status: reader.GetString(4),
                DurationMs: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                TriggeredByUserId: reader.GetGuid(6),
                ErrorMessage: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }

    public async Task<IReadOnlyList<PlatformDatabaseTableSize>> GetTableSizesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(limit, 1, 500);
        // pg_total_relation_size = data + indexes + toast. pg_relation_size
        // is data heap only. Subtract the two for an indexes+toast figure.
        const string sql = """
            select s.schemaname,
                   s.relname,
                   pg_total_relation_size(c.oid) as total_bytes,
                   pg_relation_size(c.oid) as data_bytes,
                   pg_total_relation_size(c.oid) - pg_relation_size(c.oid) as index_bytes,
                   c.reltuples::bigint as row_estimate
              from pg_stat_user_tables s
              join pg_class c on c.oid = s.relid
             order by pg_total_relation_size(c.oid) desc
             limit @limit;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("limit", clamped);

        var results = new List<PlatformDatabaseTableSize>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PlatformDatabaseTableSize(
                SchemaName: reader.GetString(0),
                TableName: reader.GetString(1),
                TotalBytes: reader.GetInt64(2),
                DataBytes: reader.GetInt64(3),
                IndexBytes: reader.GetInt64(4),
                RowEstimate: reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }

        return results;
    }

    private async Task RunPgDumpAsync(
        Guid backupId,
        string filePath,
        ConnectionInfo connectionInfo)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PgDumpExecutable,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(connectionInfo.Host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(connectionInfo.Port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--username");
        startInfo.ArgumentList.Add(connectionInfo.Username);
        startInfo.ArgumentList.Add("--dbname");
        startInfo.ArgumentList.Add(connectionInfo.Database);
        startInfo.ArgumentList.Add("--no-password");
        startInfo.ArgumentList.Add("--format=plain");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(filePath);

        if (!string.IsNullOrEmpty(connectionInfo.Password))
        {
            startInfo.Environment["PGPASSWORD"] = connectionInfo.Password;
        }

        string status;
        string? errorMessage = null;
        long? sizeBytes = null;

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("pg_dump did not start.");
            }

            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                status = PlatformDatabaseStatuses.Succeeded;
                if (File.Exists(filePath))
                {
                    sizeBytes = new FileInfo(filePath).Length;
                }
            }
            else
            {
                status = PlatformDatabaseStatuses.Failed;
                errorMessage = $"pg_dump exited with code {process.ExitCode}. {stderr.Trim()}";
                if (errorMessage.Length > 4000)
                {
                    errorMessage = errorMessage[..4000];
                }
                TryDeleteFile(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pg_dump failed for backup {BackupId}.", backupId);
            status = PlatformDatabaseStatuses.Failed;
            errorMessage = ex.Message;
            TryDeleteFile(filePath);
        }

        try
        {
            const string updateSql = """
                update platform_database_backups
                   set completed_at = now(),
                       status = @status,
                       size_bytes = @size_bytes,
                       error_message = @error_message
                 where id = @id;
                """;

            await using var connection = await _connections.OpenConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = updateSql;
            command.Parameters.AddWithValue("id", backupId);
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("size_bytes", (object?)sizeBytes ?? DBNull.Value);
            command.Parameters.AddWithValue("error_message", (object?)errorMessage ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not update backup row {BackupId} after pg_dump.", backupId);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete partial backup file {Path}.", path);
        }
    }

    private ConnectionInfo ParseConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        return new ConnectionInfo(
            Host: string.IsNullOrWhiteSpace(builder.Host) ? "127.0.0.1" : builder.Host!,
            Port: builder.Port == 0 ? 5432 : builder.Port,
            Username: string.IsNullOrWhiteSpace(builder.Username) ? "postgres" : builder.Username!,
            Password: builder.Password ?? string.Empty,
            Database: string.IsNullOrWhiteSpace(builder.Database) ? "postgres" : builder.Database!);
    }

    private sealed record ConnectionInfo(
        string Host,
        int Port,
        string Username,
        string Password,
        string Database);
}
