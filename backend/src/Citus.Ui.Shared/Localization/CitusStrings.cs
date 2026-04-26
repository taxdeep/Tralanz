namespace Citus.Ui.Shared.Localization;

/// <summary>
/// Anchor type for `IStringLocalizer&lt;CitusStrings&gt;` lookups. Sits in
/// Citus.Ui.Shared so both Blazor projects can resolve to the same
/// .resx pair (Resources/CitusStrings.en.resx + .zh.resx) via the
/// standard ASP.NET Core localization pipeline.
/// </summary>
public sealed class CitusStrings
{
}
