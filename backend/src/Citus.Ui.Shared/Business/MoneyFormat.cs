namespace Citus.Ui.Shared.Business;

/// <summary>
/// Central money formatter. All monetary display should route through here so
/// the per-company decimal precision (<see cref="BusinessCompanySummary.MoneyDecimals"/>,
/// surfaced as <c>BusinessShellState.MoneyDecimals</c>) is honoured in one
/// place. Phase 1 is display-only; configurable posting/rounding precision
/// follows in a later phase.
/// </summary>
public static class MoneyFormat
{
    /// <summary>Clamp to a supported precision (2 or 3); fall back to 2.</summary>
    public static int Normalize(int decimals) => decimals is 2 or 3 ? decimals : 2;

    /// <summary>
    /// Thousands-separated, fixed to the company precision — e.g. 2 → 1,234.50,
    /// 3 → 1,234.500. Drop-in replacement for the old <c>ToString("N2")</c>.
    /// </summary>
    public static string Amount(decimal amount, int decimals) =>
        amount.ToString("N" + Normalize(decimals));

    /// <summary>
    /// The edit/format string for a <c>RadzenNumeric.Format</c> binding —
    /// "0.00" for 2 decimals, "0.000" for 3.
    /// </summary>
    public static string EditFormat(int decimals) =>
        "0." + new string('0', Normalize(decimals));
}
