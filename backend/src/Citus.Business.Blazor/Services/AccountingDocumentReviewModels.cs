namespace Citus.Business.Blazor.Services;

public sealed record InvoicePdfDownload(byte[] Bytes, string FileName);

public sealed record InvoiceSendRequest(
    string ToEmail,
    string? Cc,
    string? Bcc,
    string? Message);

public sealed record InvoiceSendOutcome(
    bool Succeeded,
    string? ErrorMessage,
    DateTimeOffset? SentAt);

internal sealed record InvoiceSendOutcomeBody(
    bool Succeeded,
    string? Message,
    DateTimeOffset? SentAt);

public sealed record InvoiceSendHistoryEntry(
    Guid Id,
    DateTimeOffset SentAt,
    Guid SentByUserId,
    string ToEmail,
    string? CcEmails,
    string? BccEmails,
    string Subject,
    string Status,
    string? ErrorMessage);

public sealed record class AccountingDocumentReviewSummary
{
    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public CompanyId CompanyId { get; init; }

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

    public IReadOnlyList<AccountingDocumentReviewLineSummary> Lines { get; init; } = Array.Empty<AccountingDocumentReviewLineSummary>();
}

public sealed record class AccountingDocumentReviewLineSummary
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
