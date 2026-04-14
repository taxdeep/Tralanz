namespace Citus.Ui.Shared.Platform;

public sealed record class PlatformPermissionSummary
{
    public IReadOnlyList<string> Create { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Read { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Update { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Delete { get; init; } = Array.Empty<string>();
}
