namespace Citus.Ui.Shared.Atoms;

/// <summary>
/// Width presets for <c>CitusDrawer</c>. Pixel widths live in shell.css
/// under <c>.citus-drawer__panel--{sm|md|lg}</c>; bump them there if
/// the breakpoints need to retune.
/// </summary>
public enum CitusDrawerWidth
{
    Small,
    Medium,
    Large,
}
