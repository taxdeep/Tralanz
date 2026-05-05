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

        Lines = Array.AsReadOnly(materializedLines);
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
