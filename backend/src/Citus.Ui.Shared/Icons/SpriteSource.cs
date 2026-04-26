using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Citus.Ui.Shared.Icons;

/// <summary>
/// Loads tabler-sprite.svg as an embedded resource so <see cref="IconSpriteHost"/>
/// can inline its &lt;symbol&gt; definitions exactly once per rendered document.
/// </summary>
internal static partial class SpriteSource
{
    private static string? _cached;

    public static string LoadInlineSymbols()
    {
        if (_cached is not null) return _cached;

        var assembly = typeof(SpriteSource).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".tabler-sprite.svg", StringComparison.Ordinal));

        if (resourceName is null)
        {
            _cached = string.Empty;
            return _cached;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _cached = string.Empty;
            return _cached;
        }

        using var reader = new StreamReader(stream);
        var raw = reader.ReadToEnd();

        // Keep only the <symbol>…</symbol> bodies — the outer <svg> is provided
        // by IconSpriteHost itself so we don't nest two SVG roots.
        _cached = SymbolPattern().Matches(raw) is { Count: > 0 } matches
            ? string.Concat(matches.Select(m => m.Value))
            : string.Empty;
        return _cached;
    }

    [GeneratedRegex("<symbol[\\s\\S]*?</symbol>", RegexOptions.IgnoreCase)]
    private static partial Regex SymbolPattern();
}
