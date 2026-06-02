using Citus.Ui.Shared.Icons;

namespace Citus.Ui.Shared.Navigation;

public sealed record class NavMenuItem
{
    public string Title { get; init; } = string.Empty;

    public string Href { get; init; } = string.Empty;

    public IconName Icon { get; init; } = IconName.LayoutDashboard;

    public string? ModuleKey { get; init; }
}
