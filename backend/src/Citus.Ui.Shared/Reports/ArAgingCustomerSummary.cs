namespace Citus.Ui.Shared.Reports;

public sealed record class ArAgingCustomerSummary
{
    public Guid CustomerId { get; init; }

    public string CustomerEntityNumber { get; init; } = string.Empty;

    public string CustomerDisplayName { get; init; } = string.Empty;

    public bool CustomerIsActive { get; init; }

    public int OpenItemCount { get; init; }

    public DateOnly? OldestDueDate { get; init; }

    public decimal CurrentAmountBase { get; init; }

    public decimal Days1To30AmountBase { get; init; }

    public decimal Days31To60AmountBase { get; init; }

    public decimal Days61To90AmountBase { get; init; }

    public decimal DaysOver90AmountBase { get; init; }

    public decimal TotalOverdueAmountBase { get; init; }

    public decimal TotalOutstandingAmountBase { get; init; }

    public IReadOnlyList<ArAgingOpenItemSummary> OpenItems { get; init; } = Array.Empty<ArAgingOpenItemSummary>();
}
