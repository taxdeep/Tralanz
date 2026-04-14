namespace Citus.Ui.Shared.Reports;

public sealed record class ApAgingReportSummary
{
    public Guid CompanyId { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public int VendorCount { get; init; }

    public int OpenItemCount { get; init; }

    public decimal CurrentAmountBase { get; init; }

    public decimal Days1To30AmountBase { get; init; }

    public decimal Days31To60AmountBase { get; init; }

    public decimal Days61To90AmountBase { get; init; }

    public decimal DaysOver90AmountBase { get; init; }

    public decimal TotalOverdueAmountBase { get; init; }

    public decimal TotalOutstandingAmountBase { get; init; }

    public IReadOnlyList<ApAgingVendorSummary> VendorRows { get; init; } = Array.Empty<ApAgingVendorSummary>();

    public IReadOnlyList<ApAgingOpenItemSummary> DetailRows { get; init; } = Array.Empty<ApAgingOpenItemSummary>();
}
