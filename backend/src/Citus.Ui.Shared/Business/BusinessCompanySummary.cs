namespace Citus.Ui.Shared.Business;

public sealed record class BusinessCompanySummary
{
    public CompanyId Id { get; init; }

    public string CompanyCode { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool MultiCurrencyEnabled { get; init; }

    /// <summary>
    /// Mirrors <see cref="SharedKernel.CompanyAccess.CompanyAccessCompanySummary.InventoryModuleEnabled"/>.
    /// Used by the Blazor shell to gate Inventory-paid-tier UI
    /// (Items Stock kind, Receipt / Shipment / Adjustment workbenches,
    /// activation wizard entry).
    /// </summary>
    public bool InventoryModuleEnabled { get; init; }

    public string Status { get; init; } = "active";

    public bool IsReadOnly { get; init; }

    /// <summary>
    /// How many decimal places monetary amounts are shown (and rounded) with
    /// for this company — 2 (default) or 3. Drives the central money formatter
    /// so a company that trades in 3-decimal currencies can opt in.
    /// </summary>
    public int MoneyDecimals { get; init; } = 2;
}
