namespace Citus.Platform.Core.Bootstrap;

public sealed record class PlatformBootstrapReport
{
    public required int ModulesSeeded { get; init; }

    public required int EntitiesSeeded { get; init; }

    public required IReadOnlyList<string> ModuleKeys { get; init; }

    public required IReadOnlyList<string> EntityNames { get; init; }

    public DateTimeOffset BootstrappedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
