using Citus.Platform.Core.Runtime;
using Citus.Platform.Infrastructure.Persistence;
using Citus.Platform.Infrastructure.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Citus.SysAdmin.Api.Tests;

/// <summary>
/// Smoke tests for the runtime metrics service. Database sampling is
/// expected to fail (no Postgres reachable in unit tests) — the service
/// must still return a valid snapshot with a non-null Cpu / Memory /
/// Attachments record. These verify the "graceful null fallback"
/// contract the SysAdmin tile cluster relies on.
/// </summary>
public sealed class PlatformRuntimeMetricsServiceSmokeTests
{
    [Fact]
    public async Task GetSnapshot_ReturnsCpuMemoryAttachmentsEvenWhenDbUnreachable()
    {
        var connections = new PlatformPostgresConnectionFactory(
            "Host=127.0.0.1;Port=5;Database=does_not_exist;Username=test;Password=test;Pooling=false;Timeout=1");
        var attachmentOptions = Options.Create(new PlatformAttachmentStorageOptions
        {
            RootPath = null
        });
        var service = new PlatformRuntimeMetricsService(
            connections,
            attachmentOptions,
            NullLogger<PlatformRuntimeMetricsService>.Instance);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Cpu);
        Assert.True(snapshot.Cpu.LogicalProcessorCount > 0);

        Assert.NotNull(snapshot.Memory);
        Assert.True(snapshot.Memory.ProcessWorkingSetBytes > 0);
        Assert.True(snapshot.Memory.ManagedHeapBytes > 0);

        Assert.NotNull(snapshot.Database);
        Assert.False(snapshot.Database.IsReachable);
        Assert.NotNull(snapshot.Database.ErrorMessage);

        Assert.NotNull(snapshot.Attachments);
        Assert.False(snapshot.Attachments.IsProvisioned);
    }

    [Fact]
    public async Task GetSnapshot_AttachmentMetricsScansConfiguredFolder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"citus-attach-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tempDir, "a.bin"), new byte[1024]);
            await File.WriteAllBytesAsync(Path.Combine(tempDir, "b.bin"), new byte[2048]);

            var connections = new PlatformPostgresConnectionFactory(
                "Host=127.0.0.1;Port=5;Database=x;Username=u;Password=p;Pooling=false;Timeout=1");
            var options = Options.Create(new PlatformAttachmentStorageOptions { RootPath = tempDir });
            var service = new PlatformRuntimeMetricsService(
                connections, options, NullLogger<PlatformRuntimeMetricsService>.Instance);

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            Assert.True(snapshot.Attachments.IsProvisioned);
            Assert.Equal(2, snapshot.Attachments.FileCount);
            Assert.Equal(3072L, snapshot.Attachments.TotalBytes);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
