namespace Citus.Ui.Shared.Platform;

public sealed record class PlatformBootstrapReportSummary
{
    public int ModulesSeeded { get; init; }

    public int EntitiesSeeded { get; init; }

    public IReadOnlyList<string> ModuleKeys { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EntityNames { get; init; } = Array.Empty<string>();

    public DateTimeOffset BootstrappedAtUtc { get; init; }
}
