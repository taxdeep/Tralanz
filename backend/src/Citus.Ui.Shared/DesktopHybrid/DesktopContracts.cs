namespace Citus.Ui.Shared.DesktopHybrid;

public interface IDesktopFilePicker
{
    Task<DesktopPickedFile?> PickFileAsync(CancellationToken cancellationToken = default);
}

public interface IDesktopPrintService
{
    Task<DesktopOperationResult> PrintCurrentViewAsync(
        string documentName,
        CancellationToken cancellationToken = default);
}

public interface IDesktopNotificationService
{
    Task ShowAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default);
}

public interface IDesktopLocalCache
{
    Task SetStringAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default);

    Task<string?> GetStringAsync(
        string key,
        CancellationToken cancellationToken = default);
}

public interface IDesktopUpdateService
{
    Task<DesktopOperationResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
}

public interface IDesktopHostBridge
{
    Task<DesktopHostContext> GetContextAsync(CancellationToken cancellationToken = default);
}

public sealed record DesktopPickedFile(string FileName, string FullPath, long SizeBytes);

public sealed record DesktopOperationResult(bool Success, string Message);

public sealed record DesktopHostContext(
    string HostName,
    string ProfileName,
    string BusinessUrl,
    string AccountingApiUrl);
