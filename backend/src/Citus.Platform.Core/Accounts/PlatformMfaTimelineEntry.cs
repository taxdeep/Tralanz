namespace Citus.Platform.Core.Accounts;

public sealed record class PlatformMfaTimelineEntry
{
    public string Action { get; init; } = string.Empty;

    public string ActionLabel { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string ActorType { get; init; } = string.Empty;

    public string ActorDisplayName { get; init; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; init; }
}
