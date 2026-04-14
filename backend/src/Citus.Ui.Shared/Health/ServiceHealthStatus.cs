namespace Citus.Ui.Shared.Health;

public sealed record class ServiceHealthStatus
{
    public string Status { get; init; } = string.Empty;

    public string Service { get; init; } = string.Empty;

    public DateTimeOffset? Utc { get; init; }
}
