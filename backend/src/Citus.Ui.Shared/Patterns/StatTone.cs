namespace Citus.Ui.Shared.Patterns;

/// <summary>
/// Semantic tone for stat cards / badges / inline highlights. Maps to
/// the design tokens defined in tokens.css; using an enum here keeps
/// pages from inventing their own colour palettes.
/// </summary>
public enum StatTone
{
    Neutral = 0,
    Primary = 1,
    Success = 2,
    Warning = 3,
    Danger = 4,
    Info = 5
}
