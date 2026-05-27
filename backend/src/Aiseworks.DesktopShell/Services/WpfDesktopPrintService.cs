using System.Windows.Controls;
using Citus.Ui.Shared.DesktopHybrid;

namespace Aiseworks.DesktopShell.Services;

internal sealed class WpfDesktopPrintService : IDesktopPrintService
{
    public Task<DesktopOperationResult> PrintCurrentViewAsync(
        string documentName,
        CancellationToken cancellationToken = default)
    {
        var dialog = new PrintDialog();
        var accepted = dialog.ShowDialog() == true;
        var message = accepted
            ? $"Print dialog accepted for '{documentName}'. Server-generated PDFs remain the authoritative print source."
            : "Print was cancelled.";

        return Task.FromResult(new DesktopOperationResult(accepted, message));
    }
}
