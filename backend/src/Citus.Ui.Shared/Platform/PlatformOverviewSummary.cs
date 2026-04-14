namespace Citus.Ui.Shared.Platform;

public sealed record class PlatformOverviewSummary
{
    public string Name { get; init; } = string.Empty;

    public string Inspiration { get; init; } = string.Empty;

    public int ModulesRegistered { get; init; }

    public int EntitiesRegistered { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
}
