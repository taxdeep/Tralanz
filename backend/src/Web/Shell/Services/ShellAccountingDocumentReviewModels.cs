namespace Web.Shell.Services;

public sealed record class ShellAccountingDocumentReviewSummary
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

    public string ControlAccountLabel { get; init; } = string.Empty;

    public Guid? ControlAccountId { get; init; }

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public decimal SubtotalAmount { get; init; }

    public decimal TaxAmount { get; init; }

    public decimal TotalAmount { get; init; }

    public string? Memo { get; init; }

    public IReadOnlyList<ShellAccountingDocumentReviewLineSummary> Lines { get; init; } = Array.Empty<ShellAccountingDocumentReviewLineSummary>();
}

public sealed record class ShellAccountingDocumentReviewLineSummary
{
    public int LineNumber { get; init; }

    public Guid AccountId { get; init; }

    public string AccountCode { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public string AccountLabel { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public decimal? Quantity { get; init; }

    public decimal? UnitPrice { get; init; }

    public decimal LineAmount { get; init; }

    public decimal TaxAmount { get; init; }

    public bool? IsTaxRecoverable { get; init; }

    public Guid? TaxAccountId { get; init; }

    public decimal? TxDebit { get; init; }

    public decimal? TxCredit { get; init; }

    public Guid? SourceOpenItemId { get; init; }

    public string? SourceDocumentType { get; init; }

    public Guid? SourceDocumentId { get; init; }

    public string? SourceDocumentDisplayNumber { get; init; }

    public Guid? TargetOpenItemId { get; init; }

    public string? TargetDocumentType { get; init; }

    public Guid? TargetDocumentId { get; init; }

    public string? TargetDocumentDisplayNumber { get; init; }
}
