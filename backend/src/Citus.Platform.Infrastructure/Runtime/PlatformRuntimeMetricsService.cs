using System.Diagnostics;
using System.Globalization;
using Citus.Platform.Core.Runtime;
using Citus.Platform.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Citus.Platform.Infrastructure.Runtime;

/// <summary>
/// Best-effort runtime-metrics sampler — mixes Process info (cross-
/// platform), Linux <c>/proc</c> reads (CPU load average, memory),
/// and Postgres queries (db size, active connections). Every field
/// returns null when its source isn't reachable so the SysAdmin UI
/// can render a graceful "—" instead of throwing.
/// </summary>
public sealed class PlatformRuntimeMetricsService : IPlatformRuntimeMetricsService
{
    private readonly PlatformPostgresConnectionFactory _connections;
    private readonly PlatformAttachmentStorageOptions _attachmentOptions;
    private readonly ILogger<PlatformRuntimeMetricsService> _logger;

    // CPU sampling needs two readings, so we keep the previous one as
    // a baseline. Locks avoid concurrent first-sample races.
    private readonly object _cpuSampleLock = new();
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastCpuStamp;

    public PlatformRuntimeMetricsService(
        PlatformPostgresConnectionFactory connections,
        IOptions<PlatformAttachmentStorageOptions> attachmentOptions,
        ILogger<PlatformRuntimeMetricsService> logger)
    {
        _connections = connections;
        _attachmentOptions = attachmentOptions.Value;
        _logger = logger;
    }

    public async Task<PlatformRuntimeMetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var cpu = SampleCpu();
        var memory = SampleMemory();
        var database = await SampleDatabaseAsync(cancellationToken);
        var attachments = SampleAttachments();

        return new PlatformRuntimeMetricsSnapshot(
            CapturedAt: capturedAt,
            Cpu: cpu,
            Memory: memory,
            Database: database,
            Attachments: attachments);
    }

    private PlatformCpuMetrics SampleCpu()
    {
        var process = Process.GetCurrentProcess();
        var processorCount = Environment.ProcessorCount;

        double? cpuPercent = null;
        lock (_cpuSampleLock)
        {
            var now = DateTimeOffset.UtcNow;
            var totalCpu = process.TotalProcessorTime;

            if (_lastCpuStamp != default)
            {
                var wallElapsed = (now - _lastCpuStamp).TotalMilliseconds;
                var cpuElapsed = (totalCpu - _lastCpuTime).TotalMilliseconds;
                if (wallElapsed > 0 && processorCount > 0)
                {
                    // Process CPU as a percent of one core. Divide by
                    // processor count to get "CPU % of host" — matches
                    // what `top` shows in normalized mode.
                    cpuPercent = (cpuElapsed / wallElapsed / processorCount) * 100.0;
                    cpuPercent = Math.Clamp(cpuPercent.Value, 0, 100);
                }
            }

            _lastCpuTime = totalCpu;
            _lastCpuStamp = now;
        }

        double? loadAvg1m = null;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var line = File.ReadAllText("/proc/loadavg").Trim();
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    loadAvg1m = parsed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read /proc/loadavg.");
            }
        }

        return new PlatformCpuMetrics(cpuPercent, processorCount, loadAvg1m);
    }

    private PlatformMemoryMetrics SampleMemory()
    {
        var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64;
        var managed = GC.GetTotalMemory(forceFullCollection: false);

        long? hostTotal = null;
        long? hostUsed = null;

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var memInfo = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var rawLine in File.ReadLines("/proc/meminfo"))
                {
                    var colon = rawLine.IndexOf(':');
                    if (colon < 0) continue;

                    var key = rawLine[..colon].Trim();
                    var valueText = rawLine[(colon + 1)..].Trim();
                    // /proc/meminfo reports kB. Strip the suffix and parse.
                    var spaceIndex = valueText.IndexOf(' ');
                    var numericText = spaceIndex < 0 ? valueText : valueText[..spaceIndex];
                    if (long.TryParse(numericText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                    {
                        memInfo[key] = kb * 1024L;
                    }
                }

                if (memInfo.TryGetValue("MemTotal", out var total))
                {
                    hostTotal = total;
                    var available = memInfo.TryGetValue("MemAvailable", out var avail) ? avail : 0L;
                    hostUsed = Math.Max(0, total - available);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read /proc/meminfo.");
            }
        }

        return new PlatformMemoryMetrics(workingSet, managed, hostTotal, hostUsed);
    }

    private async Task<PlatformDatabaseMetrics> SampleDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select pg_database_size(current_database()),
                       (select count(*)::int from pg_stat_activity where datname = current_database());
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new PlatformDatabaseMetrics(null, null, true, null);
            }

            return new PlatformDatabaseMetrics(
                SizeBytes: reader.IsDBNull(0) ? null : reader.GetInt64(0),
                ActiveConnections: reader.IsDBNull(1) ? null : reader.GetInt32(1),
                IsReachable: true,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database metrics sample failed.");
            return new PlatformDatabaseMetrics(null, null, false, ex.Message);
        }
    }

    private PlatformAttachmentMetrics SampleAttachments()
    {
        var root = _attachmentOptions.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            return new PlatformAttachmentMetrics(null, null, IsProvisioned: false);
        }

        try
        {
            if (!Directory.Exists(root))
            {
                // Configured but not yet created on disk — still treat as
                // provisioned so the operator sees "0 files" instead of
                // "not enabled" once they've opted in.
                return new PlatformAttachmentMetrics(0L, 0, IsProvisioned: true);
            }

            long totalBytes = 0;
            var fileCount = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    totalBytes += new FileInfo(file).Length;
                    fileCount++;
                }
                catch
                {
                    // Skip individual file probe failures — keep the
                    // overall scan running.
                }
            }

            return new PlatformAttachmentMetrics(totalBytes, fileCount, IsProvisioned: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Attachment metrics scan failed for {Root}.", root);
            return new PlatformAttachmentMetrics(null, null, IsProvisioned: false);
        }
    }
}

/// <summary>
/// Configures where the platform stores customer-uploaded attachments.
/// Empty / unset means the upload feature isn't provisioned yet.
/// Operators set this via PlatformAttachments:RootPath in appsettings
/// (or the env var PlatformAttachments__RootPath); the metrics service
/// scans that directory to compute total size + file count.
/// </summary>
public sealed class PlatformAttachmentStorageOptions
{
    public const string SectionName = "PlatformAttachments";

    public string? RootPath { get; set; }
}
