using System.IO;
using Citus.Ui.Shared.DesktopHybrid;
using Microsoft.Win32;

namespace Aiseworks.DesktopShell.Services;

internal sealed class WpfDesktopFilePicker : IDesktopFilePicker
{
    public Task<DesktopPickedFile?> PickFileAsync(CancellationToken cancellationToken = default)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an Aiseworks attachment",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return Task.FromResult<DesktopPickedFile?>(null);
        }

        var info = new FileInfo(dialog.FileName);
        return Task.FromResult<DesktopPickedFile?>(
            new DesktopPickedFile(info.Name, info.FullName, info.Length));
    }
}
