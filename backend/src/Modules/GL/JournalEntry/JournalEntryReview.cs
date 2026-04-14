namespace Modules.GL.JournalEntry;

public sealed record class JournalEntryReview
{
    public required Guid Id { get; init; }

    public required Guid CompanyId { get; init; }

    public required string EntityNumber { get; init; }

    public required string DisplayNumber { get; init; }

    public required string Status { get; init; }

    public required string SourceType { get; init; }

    public required Guid SourceId { get; init; }

    public required string TransactionCurrencyCode { get; init; }

    public required string BaseCurrencyCode { get; init; }

    public required decimal ExchangeRate { get; init; }

    public required DateOnly ExchangeRateDate { get; init; }

    public required string ExchangeRateSource { get; init; }

    public Guid? FxSnapshotId { get; init; }

    public string? FxRateType { get; init; }

    public string? FxQuoteBasis { get; init; }

    public string? FxRateUseCase { get; init; }

    public string? FxPostingReason { get; init; }

    public string? FxSnapshotSemantics { get; init; }

    public string? FxSnapshotRowOrigin { get; init; }

    public string? FxProviderKey { get; init; }

    public required decimal TotalTransactionDebit { get; init; }

    public required decimal TotalTransactionCredit { get; init; }

    public required decimal TotalDebit { get; init; }

    public required decimal TotalCredit { get; init; }

    public required int LineCount { get; init; }

    public DateTimeOffset? PostedAt { get; init; }

    public DateTimeOffset? VoidedAt { get; init; }

    public DateTimeOffset? ReversedAt { get; init; }

    public required Guid CreatedByUserId { get; init; }

    public IReadOnlyList<JournalEntryReviewLine> Lines { get; init; } = Array.Empty<JournalEntryReviewLine>();

    public IReadOnlyList<JournalEntryRelatedEntry> RelatedEntries { get; init; } = Array.Empty<JournalEntryRelatedEntry>();

    public bool IsForeignCurrency =>
        !string.Equals(TransactionCurrencyCode, BaseCurrencyCode, StringComparison.OrdinalIgnoreCase);

    public bool IsBalanced =>
        TotalDebit == TotalCredit;

    public string Title => $"JE# {DisplayNumber}";

    public string SourceTypeLabel =>
        SourceType switch
        {
            "manual_journal" => "Manual journal",
            "manual_journal_void" => "Manual journal void",
            "manual_journal_reversal" => "Manual journal reversal",
            "invoice" => "Invoice",
            "bill" => "Bill",
            "credit_note" => "Credit note",
            "vendor_credit" => "Vendor credit",
            "receive_payment" => "Receive payment",
            "pay_bill" => "Pay bill",
            _ => SourceType.Replace('_', ' ')
        };

    public string FxSnapshotLabel
    {
        get
        {
            if (!IsForeignCurrency)
            {
                return "Identity base-currency posting";
            }

            return FxSnapshotId.HasValue
                ? $"Snapshot {FxSnapshotId.Value.ToString("N")[..8]}"
                : "No persisted snapshot";
        }
    }

    public string FxRateTypeLabel => string.IsNullOrWhiteSpace(FxRateType) ? "spot" : FxRateType;

    public string FxQuoteBasisLabel => string.IsNullOrWhiteSpace(FxQuoteBasis) ? "direct" : FxQuoteBasis;

    public string FxRateUseCaseLabel => string.IsNullOrWhiteSpace(FxRateUseCase) ? "general" : FxRateUseCase;

    public string FxPostingReasonLabel => string.IsNullOrWhiteSpace(FxPostingReason) ? "normal" : FxPostingReason;

    public string FxSnapshotSemanticsLabel
    {
        get
        {
            if (!IsForeignCurrency)
            {
                return "identity";
            }

            return string.IsNullOrWhiteSpace(FxSnapshotSemantics)
                ? "legacy-unavailable"
                : FxSnapshotSemantics;
        }
    }

    public string FxSnapshotRowOriginLabel
    {
        get
        {
            if (!IsForeignCurrency)
            {
                return "identity";
            }

            return string.IsNullOrWhiteSpace(FxSnapshotRowOrigin)
                ? "legacy-unavailable"
                : FxSnapshotRowOrigin;
        }
    }

    public string FxProviderLabel =>
        string.IsNullOrWhiteSpace(FxProviderKey)
            ? "No linked provider"
            : FxProviderKey;

    public string FxReviewTitle => "Posted FX review";

    public string FxReviewCaption
    {
        get
        {
            if (!IsForeignCurrency)
            {
                return "This journal entry posts in base currency without FX conversion.";
            }

            return FxSnapshotId.HasValue
                ? "This posted FX snapshot is immutable for the journal entry read path."
                : "This posted FX state has no persisted snapshot id. Review follows the stored journal header truth.";
        }
    }

    public bool HasLifecycleAlert =>
        string.Equals(Status, "voided", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "reversed", StringComparison.OrdinalIgnoreCase);

    public bool CanVoid =>
        string.Equals(Status, "posted", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SourceType, "manual_journal", StringComparison.OrdinalIgnoreCase);

    public bool CanReverse =>
        string.Equals(Status, "posted", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SourceType, "manual_journal", StringComparison.OrdinalIgnoreCase);

    public string LifecycleAlert
    {
        get
        {
            if (string.Equals(Status, "voided", StringComparison.OrdinalIgnoreCase))
            {
                return $"This journal entry is voided. Voided at {FormatTimestamp(VoidedAt)}.";
            }

            if (string.Equals(Status, "reversed", StringComparison.OrdinalIgnoreCase))
            {
                return $"This journal entry is reversed. Reversed at {FormatTimestamp(ReversedAt)}.";
            }

            return string.Empty;
        }
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "an unavailable time";
}
