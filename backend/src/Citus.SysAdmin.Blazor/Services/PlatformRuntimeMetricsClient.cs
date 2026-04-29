using System.Net.Http.Json;
using Citus.SysAdmin.Blazor.State;
using Citus.Ui.Shared.Control;

namespace Citus.SysAdmin.Blazor.Services;

public sealed class PlatformRuntimeMetricsClient(
    HttpClient httpClient,
    AppShellState shellState,
    ILogger<PlatformRuntimeMetricsClient> logger)
{
    public async Task<PlatformRuntimeMetricsSnapshotDto?> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<PlatformRuntimeMetricsSnapshotDto>(
                "control/runtime/metrics",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load runtime metrics.");
            return null;
        }
    }

    private void ApplySessionHeader()
    {
        httpClient.DefaultRequestHeaders.Remove(SysAdminAuthConstants.SessionHeaderName);
        if (shellState.IsAuthenticated)
        {
            httpClient.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, shellState.SessionToken);
        }
    }
}

public sealed record PlatformRuntimeMetricsSnapshotDto(
    DateTimeOffset CapturedAt,
    PlatformCpuMetricsDto Cpu,
    PlatformMemoryMetricsDto Memory,
    PlatformDatabaseMetricsDto Database,
    PlatformAttachmentMetricsDto Attachments);

public sealed record PlatformCpuMetricsDto(
    double? ProcessCpuPercent,
    int LogicalProcessorCount,
    double? LoadAverage1m);

public sealed record PlatformMemoryMetricsDto(
    long ProcessWorkingSetBytes,
    long ManagedHeapBytes,
    long? HostTotalBytes,
    long? HostUsedBytes);

public sealed record PlatformDatabaseMetricsDto(
    long? SizeBytes,
    int? ActiveConnections,
    bool IsReachable,
    string? ErrorMessage);

public sealed record PlatformAttachmentMetricsDto(
    long? TotalBytes,
    int? FileCount,
    bool IsProvisioned);
