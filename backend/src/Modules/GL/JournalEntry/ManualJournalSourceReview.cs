namespace Modules.GL.JournalEntry;

public sealed record class ManualJournalSourceReview
{
    public required Guid Id { get; init; }

    public required CompanyId CompanyId { get; init; }

    public required string EntityNumber { get; init; }

    public required string DisplayNumber { get; init; }

    public required string Status { get; init; }

    public required DateOnly EntryDate { get; init; }

    public required string TransactionCurrencyCode { get; init; }

    public required string BaseCurrencyCode { get; init; }

    public Guid? FxSnapshotId { get; init; }

    public required decimal FxRate { get; init; }

    public required DateOnly FxRequestedDate { get; init; }

    public required DateOnly FxEffectiveDate { get; init; }

    public required string FxSource { get; init; }

    public required string Memo { get; init; }

    public DateTimeOffset? PostedAt { get; init; }

    public required UserId CreatedByUserId { get; init; }

    public Guid? LinkedJournalEntryId { get; init; }

    public string? LinkedJournalDisplayNumber { get; init; }

    public IReadOnlyList<ManualJournalSourceReviewLine> Lines { get; init; } = Array.Empty<ManualJournalSourceReviewLine>();

    public IReadOnlyList<JournalEntryRelatedEntry> RelatedEntries { get; init; } = Array.Empty<JournalEntryRelatedEntry>();

    public bool IsForeignCurrency =>
        !string.Equals(TransactionCurrencyCode, BaseCurrencyCode, StringComparison.OrdinalIgnoreCase);

    public string Title => $"MJ# {DisplayNumber}";

    public string FxSnapshotLabel
    {
        get
        {
            if (!IsForeignCurrency)
            {
                return "Identity base-currency source";
            }

            return FxSnapshotId.HasValue
                ? $"Snapshot {FxSnapshotId.Value.ToString("N")[..8]}"
                : "No persisted snapshot";
        }
    }

    public bool HasLinkedJournalEntry =>
        LinkedJournalEntryId.HasValue && !string.IsNullOrWhiteSpace(LinkedJournalDisplayNumber);
}
