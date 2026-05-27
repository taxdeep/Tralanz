using Citus.Ui.Shared.DesktopHybrid;

namespace Aiseworks.DesktopShell.Services;

internal sealed class WpfDesktopUpdateService : IDesktopUpdateService
{
    public Task<DesktopOperationResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            new DesktopOperationResult(
                Success: true,
                Message: "Update channel is ready for MSIX, ClickOnce, or enterprise software distribution integration."));
    }
}
