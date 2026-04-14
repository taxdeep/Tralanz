namespace Citus.Ui.Shared.Reports;

public sealed record class ArAgingOpenItemSummary
{
    public Guid OpenItemId { get; init; }

    public Guid CustomerId { get; init; }

    public string CustomerEntityNumber { get; init; } = string.Empty;

    public string CustomerDisplayName { get; init; } = string.Empty;

    public bool CustomerIsActive { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public Guid SourceDocumentId { get; init; }

    public string DisplayNumber { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public int DaysPastDue { get; init; }

    public string AgingBucket { get; init; } = string.Empty;

    public string DocumentCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public string BalanceSide { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public decimal OriginalAmountTx { get; init; }

    public decimal OriginalAmountBase { get; init; }

    public decimal OpenAmountTx { get; init; }

    public decimal OpenAmountBase { get; init; }

    public decimal SignedOpenAmountTx { get; init; }

    public decimal SignedOpenAmountBase { get; init; }
}
