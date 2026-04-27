using Radzen;

namespace Citus.Ui.Shared.Services;

/// <summary>
/// Phase 7 of the UI library migration. Drop-in replacement for the
/// AntDesign IMessageService surface that was @inject'd across ~25
/// pages. Wraps Radzen.NotificationService so existing call shapes
/// (Message.InfoAsync(text), .WarningAsync(text), .SuccessAsync(text),
/// .ErrorAsync(text)) keep working with a single sed across @inject
/// lines.
///
/// Stays async-returning Task — both because every call site already
/// awaits, and because that lets us swap in latency-aware behaviour
/// later (rate-limit, queue, etc.) without touching call sites.
/// </summary>
public sealed class CitusToastService
{
    private readonly NotificationService _notifications;

    public CitusToastService(NotificationService notifications)
    {
        _notifications = notifications;
    }

    public Task InfoAsync(string message) => NotifyAsync(NotificationSeverity.Info, message);

    public Task WarningAsync(string message) => NotifyAsync(NotificationSeverity.Warning, message);

    public Task SuccessAsync(string message) => NotifyAsync(NotificationSeverity.Success, message);

    public Task ErrorAsync(string message) => NotifyAsync(NotificationSeverity.Error, message);

    private Task NotifyAsync(NotificationSeverity severity, string message)
    {
        _notifications.Notify(new NotificationMessage
        {
            Severity = severity,
            Summary = message,
            Duration = severity == NotificationSeverity.Error ? 6000 : 4000
        });
        return Task.CompletedTask;
    }
}
