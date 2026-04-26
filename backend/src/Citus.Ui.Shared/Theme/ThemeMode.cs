namespace Citus.Ui.Shared.Theme;

/// <summary>
/// Effective theme mode resolved for the current user/session. <see cref="System"/>
/// means the browser decides via prefers-color-scheme; <see cref="Light"/> and
/// <see cref="Dark"/> are explicit user choices that override the OS preference.
/// </summary>
public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}
