namespace Citus.Ui.Shared.Control;

public sealed record class MaintenanceUpdateRequest
{
    public bool Enabled { get; init; }

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset? ScheduledUntilUtc { get; init; }
}
