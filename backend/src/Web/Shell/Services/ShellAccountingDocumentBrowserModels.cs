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

    public int? BillReceiptOpenDiscrepancyCount { get; init; }

    public string? BillReceiptInvestigationSummary { get; init; }

    public string? InvoiceIssueMatchStatus { get; init; }

    public string? InvoiceIssuePostingGateLabel { get; init; }

    public string? InvoiceIssuePostingGateSummary { get; init; }

    public bool? InvoiceIssueAllowsPost { get; init; }

    public string? InvoiceShipmentMatchStatus { get; init; }

    public string? InvoiceShipmentPostingGateLabel { get; init; }

    public string? InvoiceShipmentPostingGateSummary { get; init; }

    public bool? InvoiceShipmentAllowsPost { get; init; }

    public string? InvoiceCoverageStatus { get; init; }

    public string? InvoiceCoverageSummary { get; init; }
}
