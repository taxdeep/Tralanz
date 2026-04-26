using Microsoft.JSInterop;

namespace Citus.Ui.Shared.Theme;

/// <summary>
/// Scoped per-circuit service. Holds the resolved theme mode and bridges
/// state changes to the JS module that toggles the &lt;html&gt; class and
/// persists the user's choice (cookie for SSR, localStorage for SPA).
/// </summary>
public sealed class ThemeService : IThemeService, IAsyncDisposable
{
    private const string ModulePath = "./_content/Citus.Ui.Shared/citus-theme.js";

    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;
    private bool _initialized;

    public ThemeService(IJSRuntime js, ThemeCookieReader cookieReader)
    {
        _js = js;
        Mode = cookieReader.Mode;
        IsDark = cookieReader.IsDark;
    }

    public ThemeMode Mode { get; private set; }

    public bool IsDark { get; private set; }

    public event Action? ThemeChanged;

    public async Task InitializeInteractiveAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        _initialized = true;

        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
        var resolved = await _module.InvokeAsync<ThemeStateDto>("init", cancellationToken, (int)Mode);
        ApplyResolved(resolved);
    }

    public async Task SetModeAsync(ThemeMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == Mode && _initialized) return;

        if (_module is null)
        {
            _module = await _js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
            _initialized = true;
        }

        var resolved = await _module.InvokeAsync<ThemeStateDto>("set", cancellationToken, (int)mode);
        ApplyResolved(resolved);
    }

    private void ApplyResolved(ThemeStateDto state)
    {
        var changed = Mode != (ThemeMode)state.Mode || IsDark != state.IsDark;
        Mode = (ThemeMode)state.Mode;
        IsDark = state.IsDark;
        if (changed)
        {
            ThemeChanged?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { /* circuit gone, nothing to dispose */ }
        }
    }

    private sealed record ThemeStateDto(int Mode, bool IsDark);
}
