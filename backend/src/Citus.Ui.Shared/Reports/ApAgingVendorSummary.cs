namespace Citus.Ui.Shared.Reports;

public sealed record class ApAgingVendorSummary
{
    public Guid VendorId { get; init; }

    public string VendorEntityNumber { get; init; } = string.Empty;

    public string VendorDisplayName { get; init; } = string.Empty;

    public bool VendorIsActive { get; init; }

    public int OpenItemCount { get; init; }

    public DateOnly? OldestDueDate { get; init; }

    public decimal CurrentAmountBase { get; init; }

    public decimal Days1To30AmountBase { get; init; }

    public decimal Days31To60AmountBase { get; init; }

    public decimal Days61To90AmountBase { get; init; }

    public decimal DaysOver90AmountBase { get; init; }

    public decimal TotalOverdueAmountBase { get; init; }

    public decimal TotalOutstandingAmountBase { get; init; }

    public IReadOnlyList<ApAgingOpenItemSummary> OpenItems { get; init; } = Array.Empty<ApAgingOpenItemSummary>();
}
