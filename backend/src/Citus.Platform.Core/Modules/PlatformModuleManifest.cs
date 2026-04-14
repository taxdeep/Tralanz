namespace Citus.Platform.Core.Modules;

public sealed record class PlatformModuleManifest
{
    public required Guid Id { get; init; }

    public required string Key { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public string RoutePrefix { get; init; } = string.Empty;

    public bool IsSystemModule { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EntityNames { get; init; } = Array.Empty<string>();
}
