namespace Modules.GL.JournalEntry;

public interface IJournalEntryReviewStore
{
    Task<IReadOnlyList<JournalEntryReviewListItem>> ListRecentAsync(
        Guid companyId,
        int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalLedgerAccountBalance>> ListAccountBalancesAsync(
        Guid companyId,
        DateOnly throughDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalLedgerEntryReviewItem>> ListLedgerEntriesAsync(
        Guid companyId,
        Guid accountId,
        int take,
        CancellationToken cancellationToken);

    Task<JournalEntryReview?> GetAsync(
        Guid companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken);
}

public sealed record class JournalEntryReviewListItem(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    string SourceType,
    Guid SourceId,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal ExchangeRate,
    Guid? FxSnapshotId,
    decimal TotalTransactionDebit,
    decimal TotalTransactionCredit,
    decimal TotalDebit,
    decimal TotalCredit,
    int LineCount,
    DateTimeOffset? PostedAt,
    DateTimeOffset? VoidedAt,
    DateTimeOffset? ReversedAt)
{
    public bool IsForeignCurrency =>
        !string.Equals(TransactionCurrencyCode, BaseCurrencyCode, StringComparison.OrdinalIgnoreCase);

    public bool IsBalanced =>
        TotalDebit == TotalCredit && TotalTransactionDebit == TotalTransactionCredit;

    public string SourceIdShort =>
        SourceId == Guid.Empty ? "no source" : SourceId.ToString("N")[..8];

    public string FxTraceLabel
    {
        get
        {
            if (!IsForeignCurrency)
            {
                return "identity";
            }

            return FxSnapshotId.HasValue
                ? $"snapshot {FxSnapshotId.Value.ToString("N")[..8]}"
                : "header-only";
        }
    }

    public string SourceTypeLabel =>
        SourceType switch
        {
            "manual_journal" => "Manual journal",
            "manual_journal_void" => "Manual journal void",
            "manual_journal_reversal" => "Manual journal reversal",
            "invoice" => "Invoice",
            "invoice_reversal" => "Invoice reversal",
            "bill" => "Bill",
            "bill_reversal" => "Bill reversal",
            "credit_note" => "Credit note",
            "credit_note_reversal" => "Credit note reversal",
            "vendor_credit" => "Vendor credit",
            "vendor_credit_reversal" => "Vendor credit reversal",
            "receive_payment" => "Receive payment",
            "receive_payment_reversal" => "Receive payment reversal",
            "pay_bill" => "Pay bill",
            "pay_bill_reversal" => "Pay bill reversal",
            "credit_application" => "Credit application",
            "vendor_credit_application" => "Vendor credit application",
            "fx_revaluation" => "FX revaluation",
            _ => SourceType.Replace('_', ' ')
        };
}

public sealed record class JournalLedgerCurrencyExposure(
    Guid AccountId,
    string CurrencyCode,
    decimal TransactionDebit,
    decimal TransactionCredit,
    decimal BaseDebit,
    decimal BaseCredit,
    int EntryCount)
{
    public decimal TransactionNet => TransactionDebit - TransactionCredit;

    public decimal BaseNet => BaseDebit - BaseCredit;
}

public sealed record class JournalLedgerAccountBalance(
    Guid AccountId,
    string AccountCode,
    string AccountName,
    string RootType,
    string DetailType,
    decimal TotalDebit,
    decimal TotalCredit,
    int LedgerEntryCount,
    IReadOnlyList<JournalLedgerCurrencyExposure> CurrencyExposures)
{
    public decimal NetDebit =>
        Math.Max(TotalDebit - TotalCredit, 0m);

    public decimal NetCredit =>
        Math.Max(TotalCredit - TotalDebit, 0m);

    public bool HasForeignCurrencyExposure =>
        CurrencyExposures.Count > 1 ||
        CurrencyExposures.Any(exposure => !string.Equals(exposure.CurrencyCode, "BASE", StringComparison.OrdinalIgnoreCase));
}

public sealed record class JournalLedgerEntryReviewItem(
    Guid LedgerEntryId,
    Guid JournalEntryId,
    Guid JournalEntryLineId,
    DateOnly PostingDate,
    string JournalEntryDisplayNumber,
    string JournalEntryStatus,
    string SourceType,
    Guid SourceId,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal ExchangeRate,
    Guid? FxSnapshotId,
    decimal TransactionDebit,
    decimal TransactionCredit,
    decimal Debit,
    decimal Credit,
    string Description)
{
    public decimal BaseNet => Debit - Credit;

    public decimal TransactionNet => TransactionDebit - TransactionCredit;

    public bool IsForeignCurrency =>
        !string.Equals(TransactionCurrencyCode, BaseCurrencyCode, StringComparison.OrdinalIgnoreCase);

    public string SourceIdShort =>
        SourceId == Guid.Empty ? "no source" : SourceId.ToString("N")[..8];

    public string FxTraceLabel
    {
        get
        {
            if (!IsForeignCurrency)
            {
                return "identity";
            }

            return FxSnapshotId.HasValue
                ? $"snapshot {FxSnapshotId.Value.ToString("N")[..8]}"
                : "header-only";
        }
    }

    public string SourceTypeLabel =>
        SourceType switch
        {
            "manual_journal" => "Manual journal",
            "manual_journal_void" => "Manual journal void",
            "manual_journal_reversal" => "Manual journal reversal",
            "invoice" => "Invoice",
            "invoice_reversal" => "Invoice reversal",
            "bill" => "Bill",
            "bill_reversal" => "Bill reversal",
            "credit_note" => "Credit note",
            "credit_note_reversal" => "Credit note reversal",
            "vendor_credit" => "Vendor credit",
            "vendor_credit_reversal" => "Vendor credit reversal",
            "receive_payment" => "Receive payment",
            "receive_payment_reversal" => "Receive payment reversal",
            "pay_bill" => "Pay bill",
            "pay_bill_reversal" => "Pay bill reversal",
            "credit_application" => "Credit application",
            "vendor_credit_application" => "Vendor credit application",
            "fx_revaluation" => "FX revaluation",
            _ => SourceType.Replace('_', ' ')
        };
}
