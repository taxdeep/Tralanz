namespace SharedKernel.CompanyAccess;

public sealed record class CompanyAccessCompanySummary
{
    public Guid Id { get; init; }

    public string CompanyCode { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool MultiCurrencyEnabled { get; init; }

    /// <summary>
    /// Drives the Items page Stock-kind gate, the Receipt / Shipment /
    /// Adjustment workbench visibility, and any other UI affordance
    /// that should only appear when the company has paid for the
    /// Inventory add-on. Defaults to <c>false</c>; flipped on by the
    /// activation wizard.
    /// </summary>
    public bool InventoryModuleEnabled { get; init; }

    public string Status { get; init; } = "active";

    public bool IsReadOnly { get; init; }
}
