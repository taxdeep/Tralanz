using Citus.Ui.Shared.Icons;
using Microsoft.AspNetCore.Components;

namespace Citus.Ui.Shared.Patterns;

/// <summary>
/// One entry in a <see cref="CitusMoreMenu"/>. Mutable init-only
/// surface so callers can build a list inline without having to
/// thread an EventCallback through a record-positional ctor (records
/// don't compose well with EventCallback because the type parameter
/// changes with each callback signature). Set <see cref="Disabled"/>
/// to grey-out without removing the row (operators read the menu and
/// expect to see the action even when it's not currently allowed —
/// the disabled state IS the explanation).
/// </summary>
public sealed class CitusMoreMenuItem
{
    /// <summary>Visible label, e.g. "Copy", "Void".</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Optional leading icon.</summary>
    public IconName? Icon { get; init; }

    /// <summary>Click handler. Required for the item to do anything useful.</summary>
    public EventCallback OnClick { get; init; }

    /// <summary>
    /// When true the row is rendered greyed and clicks are no-ops.
    /// Use for state-conditional rows like "Void" being unavailable on
    /// an already-voided document.
    /// </summary>
    public bool Disabled { get; init; }

    /// <summary>
    /// When true the row uses the danger color. Reserved for
    /// destructive actions (Void, Delete) so the operator's eye lands
    /// on them last.
    /// </summary>
    public bool Danger { get; init; }
}
