using Citus.Ui.Shared.DesktopHybrid;

namespace Aiseworks.DesktopShell.Services;

internal sealed class WpfDesktopHostBridge : IDesktopHostBridge
{
    public Task<DesktopHostContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            new DesktopHostContext(
                HostName: Environment.MachineName,
                ProfileName: "Local Docker Test Stack",
                BusinessUrl: "http://localhost:18080",
                AccountingApiUrl: "http://localhost:15088"));
    }
}
