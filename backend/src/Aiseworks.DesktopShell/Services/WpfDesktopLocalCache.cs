using System.IO;
using Citus.Ui.Shared.DesktopHybrid;

namespace Aiseworks.DesktopShell.Services;

internal sealed class WpfDesktopLocalCache : IDesktopLocalCache
{
    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Aiseworks",
        "DesktopHybrid",
        "Cache");

    public async Task SetStringAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(GetPath(key), value, cancellationToken);
    }

    public async Task<string?> GetStringAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : null;
    }

    private string GetPath(string key)
    {
        var safeName = string.Join(
            "_",
            key.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

        return Path.Combine(_cacheDirectory, $"{safeName}.txt");
    }
}
