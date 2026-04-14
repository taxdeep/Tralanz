namespace Citus.Ui.Shared.Shell;

public sealed record class MaintenanceStateSummary
{
    public bool Enabled { get; init; }

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset? ScheduledUntilUtc { get; init; }
}
