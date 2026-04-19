namespace Web.Shell.Services;

public sealed record class ShellAccountingSourceDocumentBrowserItem
{
    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public Guid CompanyId { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public string CounterpartyLabel { get; init; } = string.Empty;

    public Guid? CounterpartyId { get; init; }

    public string? CounterpartyDisplayName { get; init; }

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public decimal TotalAmount { get; init; }

    public string? BillReceiptMatchStatus { get; init; }

    public string? BillReceiptPostingGateLabel { get; init; }

    public string? BillReceiptPostingGateSummary { get; init; }

    public bool? BillReceiptAllowsPost { get; init; }
}
