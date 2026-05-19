using Citus.Ui.Shared.Icons;

namespace Citus.Ui.Shared.Navigation;

public sealed record class NavMenuItem
{
    public string Title { get; init; } = string.Empty;

    public string Href { get; init; } = string.Empty;

    public IconName Icon { get; init; } = IconName.LayoutDashboard;

    /// <summary>
    /// Optional per-company module-flag key. When non-null the nav
    /// menu hides the item unless the company has the matching module
    /// enabled. Always-on items leave this null and render
    /// unconditionally.
    /// </summary>
    public string? ModuleKey { get; init; }
}
