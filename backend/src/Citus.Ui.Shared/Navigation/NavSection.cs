namespace Citus.Ui.Shared.Navigation;

public sealed record class NavSection
{
    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<NavMenuItem> Items { get; init; } = Array.Empty<NavMenuItem>();
}
