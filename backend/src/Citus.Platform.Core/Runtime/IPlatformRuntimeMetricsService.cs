namespace Citus.Platform.Core.Runtime;

/// <summary>
/// Snapshot of host-level + database resource consumption surfaced on
/// the SysAdmin Overview tile cluster. Implementation pulls from the
/// running process (CPU / GC), the host kernel (Linux /proc), and a
/// few cheap Postgres queries; every field is best-effort, returning
/// null if the underlying source can't be sampled (e.g. /proc on
/// Windows dev box).
/// </summary>
public interface IPlatformRuntimeMetricsService
{
    Task<PlatformRuntimeMetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public sealed record PlatformRuntimeMetricsSnapshot(
    DateTimeOffset CapturedAt,
    PlatformCpuMetrics Cpu,
    PlatformMemoryMetrics Memory,
    PlatformDatabaseMetrics Database,
    PlatformAttachmentMetrics Attachments);

public sealed record PlatformCpuMetrics(
    /// <summary>Process-level CPU time as % of one wall-clock second, averaged over the sampling window. Null if not yet warm.</summary>
    double? ProcessCpuPercent,
    /// <summary>Total available logical processors on the host.</summary>
    int LogicalProcessorCount,
    /// <summary>Linux 1-minute load average. Null on non-Linux hosts.</summary>
    double? LoadAverage1m);

public sealed record PlatformMemoryMetrics(
    /// <summary>Working set of the dotnet process — what `top` shows. In bytes.</summary>
    long ProcessWorkingSetBytes,
    /// <summary>Managed-heap allocation (GC-tracked). In bytes.</summary>
    long ManagedHeapBytes,
    /// <summary>Host total physical memory. Linux only. Null elsewhere.</summary>
    long? HostTotalBytes,
    /// <summary>Host memory currently used (total minus available). Linux only.</summary>
    long? HostUsedBytes);

public sealed record PlatformDatabaseMetrics(
    /// <summary>Result of pg_database_size(current_database()) in bytes.</summary>
    long? SizeBytes,
    /// <summary>Connection count seen by pg_stat_activity for this database.</summary>
    int? ActiveConnections,
    /// <summary>Whether the metrics call could connect at all.</summary>
    bool IsReachable,
    string? ErrorMessage);

public sealed record PlatformAttachmentMetrics(
    /// <summary>Bytes occupied by stored attachments. Null if attachment storage is not yet provisioned.</summary>
    long? TotalBytes,
    /// <summary>Number of attachment files. Null if not provisioned.</summary>
    int? FileCount,
    /// <summary>Whether the attachment storage backend is configured.</summary>
    bool IsProvisioned);
