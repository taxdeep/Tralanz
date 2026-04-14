namespace Citus.Platform.Core.Runtime;

public sealed record class PlatformMaintenanceState
{
    public bool Enabled { get; init; }

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset? ScheduledUntilUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
