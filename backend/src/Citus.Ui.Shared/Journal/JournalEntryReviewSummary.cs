namespace Citus.Ui.Shared.Journal;

public sealed record class JournalEntryReviewSummary
{
    public Guid Id { get; init; }

    public CompanyId CompanyId { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid SourceId { get; init; }

    public DateOnly? SourceDocumentDate { get; init; }

    public string SourceMemo { get; init; } = string.Empty;

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public decimal ExchangeRate { get; init; }

    public DateOnly ExchangeRateDate { get; init; }

    public string ExchangeRateSource { get; init; } = string.Empty;

    public Guid? FxRateSnapshotId { get; init; }

    public decimal TotalTxDebit { get; init; }

    public decimal TotalTxCredit { get; init; }

    public decimal TotalDebit { get; init; }

    public decimal TotalCredit { get; init; }

    public int LineCount { get; init; }

    public DateTimeOffset? PostedAt { get; init; }

    public DateTimeOffset? VoidedAt { get; init; }

    public DateTimeOffset? ReversedAt { get; init; }

    public UserId CreatedByUserId { get; init; }

    public IReadOnlyList<JournalEntryReviewLineSummary> Lines { get; init; } = Array.Empty<JournalEntryReviewLineSummary>();
}
