using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;

namespace Citus.Accounting.Domain.Journal;

public sealed record JournalEntryDraftLine(
    int LineNumber,
    Guid AccountId,
    string Description,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit,
    string? TaxComponentType = null,
    string? ControlRole = null,
    Guid? PartyId = null,
    string? PostingRole = null,
    int? SourceLineNumber = null);

public sealed class JournalEntryDraft
{
    public JournalEntryDraft(
        CompanyId companyId,
        string sourceType,
        Guid sourceId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef fxSnapshot,
        IEnumerable<JournalEntryDraftLine> lines)
    {
        CompanyId = companyId;
        SourceType = string.IsNullOrWhiteSpace(sourceType) ? throw new ArgumentException("Source type is required.", nameof(sourceType)) : sourceType.Trim();
        SourceId = sourceId == Guid.Empty ? throw new ArgumentException("Source id is required.", nameof(sourceId)) : sourceId;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot ?? throw new ArgumentNullException(nameof(fxSnapshot));

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Journal entry draft must contain lines.");
        }

        Lines = Array.AsReadOnly(materializedLines);
    }

    public CompanyId CompanyId { get; }

    public string SourceType { get; }

    public Guid SourceId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef FxSnapshot { get; }

    public IReadOnlyList<JournalEntryDraftLine> Lines { get; }

    public decimal TotalDebit => Lines.Sum(static line => line.Debit);

    public decimal TotalCredit => Lines.Sum(static line => line.Credit);
}

public sealed record JournalEntryLine(
    int LineNumber,
    Guid AccountId,
    string Description,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit,
    string? TaxComponentType = null,
    string? ControlRole = null,
    Guid? PartyId = null,
    string? PostingRole = null,
    int? SourceLineNumber = null);

public sealed class JournalEntry
{
    public JournalEntry(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        string sourceType,
        Guid sourceId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef fxSnapshot,
        IEnumerable<JournalEntryLine> lines,
        PostingRunId postingRunId,
        string idempotencyKey)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        SourceType = string.IsNullOrWhiteSpace(sourceType) ? throw new ArgumentException("Source type is required.", nameof(sourceType)) : sourceType.Trim();
        SourceId = sourceId == Guid.Empty ? throw new ArgumentException("Source id is required.", nameof(sourceId)) : sourceId;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot ?? throw new ArgumentNullException(nameof(fxSnapshot));
        PostingRunId = postingRunId;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey)) : idempotencyKey.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Journal entry must contain at least one line.");
        }

        // M6: defensive double-entry check at the domain layer. The
        // authoritative balance enforcement still happens in
        // DefaultPostingSupport.EnsureJournalInvariants (full invariants
        // including FX snapshot + per-fragment validation) and
        // PostgresJournalEntryWriter.EnsureDraftIsBalanced. This ctor
        // check fires earlier — any caller that materializes a
        // JournalEntry domain object with unbalanced lines gets a clear
        // error at the point of construction rather than at write time.
        EnsureBalanced(materializedLines);

        Lines = Array.AsReadOnly(materializedLines);
    }

    /// <summary>
    /// Both transaction-currency and base-currency balance enforced
    /// with 6-decimal rounding tolerance to match
    /// <c>PostgreSqlJournalEntryWriter</c>'s precision band. The
    /// realized-FX edge case (TxDebit = TxCredit = 0 with a non-zero
    /// base leg) naturally passes here because 0 − 0 = 0 in TX.
    /// </summary>
    private static void EnsureBalanced(IReadOnlyList<JournalEntryLine> lines)
    {
        var txDebit = lines.Sum(static l => l.TxDebit);
        var txCredit = lines.Sum(static l => l.TxCredit);
        var txDelta = Math.Round(txDebit - txCredit, 6, MidpointRounding.ToEven);
        if (txDelta != 0m)
        {
            throw new InvalidOperationException(
                $"Journal entry is not balanced in transaction currency. Delta: {txDelta:0.00####}.");
        }

        var baseDebit = lines.Sum(static l => l.Debit);
        var baseCredit = lines.Sum(static l => l.Credit);
        var baseDelta = Math.Round(baseDebit - baseCredit, 6, MidpointRounding.ToEven);
        if (baseDelta != 0m)
        {
            throw new InvalidOperationException(
                $"Journal entry is not balanced in base currency. Delta: {baseDelta:0.00####}.");
        }
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string Status { get; }

    public string SourceType { get; }

    public Guid SourceId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef FxSnapshot { get; }

    public PostingRunId PostingRunId { get; }

    public string IdempotencyKey { get; }

    public IReadOnlyList<JournalEntryLine> Lines { get; }
}
