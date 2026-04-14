namespace Citus.Ui.Shared.Platform;

public sealed record class PlatformModuleSummary
{
    public Guid Id { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string RoutePrefix { get; init; } = string.Empty;

    public bool IsSystemModule { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EntityNames { get; init; } = Array.Empty<string>();
}
