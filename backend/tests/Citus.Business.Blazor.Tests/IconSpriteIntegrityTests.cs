using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Citus.Ui.Shared.Icons;

namespace Citus.Business.Blazor.Tests;

/// <summary>
/// Guards the contract between IconName, IconNameMap, and tabler-sprite.svg.
/// If any of the three drift, callers of &lt;CitusIcon&gt; would render
/// invisible &lt;use&gt; references — this test catches that at build time.
/// </summary>
public sealed class IconSpriteIntegrityTests
{
    [Fact]
    public void EveryIconNameHasASpriteSymbol()
    {
        var sprite = LoadSprite();
        var symbolIds = ExtractSymbolIds(sprite);

        foreach (var name in Enum.GetValues<IconName>())
        {
            var expectedId = IconNameMap.SpriteIds[name];
            Assert.True(
                symbolIds.Contains(expectedId),
                $"Sprite is missing symbol '{expectedId}' for IconName.{name}.");
        }
    }

    [Fact]
    public void IconNameMapIsExhaustive()
    {
        foreach (var name in Enum.GetValues<IconName>())
        {
            Assert.True(
                IconNameMap.SpriteIds.ContainsKey(name),
                $"IconNameMap.SpriteIds is missing IconName.{name}.");
        }
    }

    private static string LoadSprite()
    {
        var assembly = typeof(IconName).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(".tabler-sprite.svg", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static HashSet<string> ExtractSymbolIds(string sprite)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(sprite, "<symbol[^>]*id=\"([^\"]+)\""))
        {
            ids.Add(match.Groups[1].Value);
        }
        return ids;
    }
}
