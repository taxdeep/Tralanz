using System.Windows;
using Citus.Ui.Shared.DesktopHybrid;

namespace Aiseworks.DesktopShell.Services;

internal sealed class WpfDesktopNotificationService : IDesktopNotificationService
{
    public Task ShowAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }
}
