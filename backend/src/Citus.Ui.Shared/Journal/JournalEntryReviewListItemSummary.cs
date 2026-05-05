namespace Citus.Ui.Shared.Journal;

public sealed record class JournalEntryReviewListItemSummary
{
    public Guid Id { get; init; }

    public CompanyId CompanyId { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid SourceId { get; init; }

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public decimal TotalTxDebit { get; init; }

    public decimal TotalTxCredit { get; init; }

    public decimal TotalDebit { get; init; }

    public decimal TotalCredit { get; init; }

    public int LineCount { get; init; }

    public DateTimeOffset? PostedAt { get; init; }

    public DateTimeOffset? VoidedAt { get; init; }

    public DateTimeOffset? ReversedAt { get; init; }
}
