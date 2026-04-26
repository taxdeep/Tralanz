namespace Citus.Ui.Shared.Theme;

/// <summary>
/// Single source of truth for the active light/dark theme. Components that
/// react to theme changes (e.g. AntDesign ConfigProvider) subscribe to
/// <see cref="ThemeChanged"/>; components that toggle the theme call
/// <see cref="SetModeAsync"/>.
/// </summary>
public interface IThemeService
{
    /// <summary>User preference (System / Light / Dark).</summary>
    ThemeMode Mode { get; }

    /// <summary>Effective resolved value, never <see cref="ThemeMode.System"/>.</summary>
    bool IsDark { get; }

    /// <summary>Fired after <see cref="Mode"/> changes.</summary>
    event Action? ThemeChanged;

    /// <summary>
    /// Persist the user's choice (cookie + localStorage) and apply it to the
    /// document root. Safe to call from interactive components.
    /// </summary>
    Task SetModeAsync(ThemeMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Wire the service up after the first interactive render. The Razor host
    /// is responsible for calling this exactly once.
    /// </summary>
    Task InitializeInteractiveAsync(CancellationToken cancellationToken = default);
}
