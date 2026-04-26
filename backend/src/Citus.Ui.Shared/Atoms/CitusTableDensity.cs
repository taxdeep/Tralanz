namespace Citus.Ui.Shared.Atoms;

/// <summary>
/// Three table densities (UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC §6).
/// Use <see cref="Compact"/> for accounting ledgers and review screens,
/// <see cref="Default"/> for most lists, <see cref="Comfortable"/> when
/// rows carry rich content (multi-line addresses, attachments).
/// </summary>
public enum CitusTableDensity
{
    Compact = 0,
    Default = 1,
    Comfortable = 2
}
