namespace Citus.Platform.Core.Metadata;

public sealed record class CoreEntityPermissionSet
{
    public IReadOnlyList<string> Create { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Read { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Update { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Delete { get; init; } = Array.Empty<string>();
}
