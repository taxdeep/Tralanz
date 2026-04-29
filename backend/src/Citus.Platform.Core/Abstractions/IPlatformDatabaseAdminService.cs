namespace Citus.Platform.Core.Abstractions;

/// <summary>
/// SysAdmin-facing operations on the Postgres database itself —
/// backups via pg_dump, VACUUM ANALYZE, and per-table size reporting.
/// All long-running work runs fire-and-forget on a background Task and
/// updates a tracking row when done; the SysAdmin UI polls
/// list endpoints to render progress / completion.
/// </summary>
public interface IPlatformDatabaseAdminService
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Kicks off a pg_dump in the background and returns the freshly
    /// created tracking row (status='running'). The caller does not
    /// block on the dump completing — the row's status flips to
    /// 'succeeded' or 'failed' when the spawned process exits.
    /// </summary>
    Task<PlatformDatabaseBackupRecord> StartBackupAsync(
        Guid triggeredByUserId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformDatabaseBackupRecord>> ListBackupsAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<PlatformDatabaseBackupRecord?> GetBackupAsync(
        Guid backupId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs VACUUM (ANALYZE) on the current database. Synchronous from
    /// the caller's perspective — the SQL command itself runs to
    /// completion before returning so the caller can render the final
    /// status. VACUUM is internally chunked by Postgres and yields
    /// regularly so concurrent business writes are not blocked.
    /// </summary>
    Task<PlatformDatabaseMaintenanceRun> RunVacuumAnalyzeAsync(
        Guid triggeredByUserId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformDatabaseMaintenanceRun>> ListMaintenanceRunsAsync(
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Per-user-table size report sourced from pg_stat_user_tables +
    /// pg_total_relation_size. Used to populate the "Table sizes" panel
    /// so operators can see which tables are growing.
    /// </summary>
    Task<IReadOnlyList<PlatformDatabaseTableSize>> GetTableSizesAsync(
        int limit,
        CancellationToken cancellationToken);
}

public sealed record PlatformDatabaseBackupRecord(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string? FilePath,
    long? SizeBytes,
    Guid TriggeredByUserId,
    string? ErrorMessage);

public sealed record PlatformDatabaseMaintenanceRun(
    Guid Id,
    string Operation,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    long? DurationMs,
    Guid TriggeredByUserId,
    string? ErrorMessage);

public sealed record PlatformDatabaseTableSize(
    string SchemaName,
    string TableName,
    long TotalBytes,
    long DataBytes,
    long IndexBytes,
    long? RowEstimate);

public static class PlatformDatabaseStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public static class PlatformDatabaseOperations
{
    public const string VacuumAnalyze = "vacuum_analyze";
}

/// <summary>
/// Where pg_dump output gets written. Defaults to /opt/citus/backups
/// (the Ubuntu 24.04 install script already provisions that
/// directory). Override via <c>PlatformDatabase:BackupRootPath</c>.
/// </summary>
public sealed class PlatformDatabaseAdminOptions
{
    public const string SectionName = "PlatformDatabase";

    public string BackupRootPath { get; set; } = "/opt/citus/backups";

    /// <summary>Filename of the pg_dump binary. Lets Windows dev boxes
    /// point at a local install if needed.</summary>
    public string PgDumpExecutable { get; set; } = "pg_dump";
}
