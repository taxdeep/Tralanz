namespace Citus.Ui.Shared.Journal;

public sealed record class JournalEntryReviewLineSummary
{
    public Guid LineId { get; init; }

    public int LineNumber { get; init; }

    public Guid AccountId { get; init; }

    public string AccountCode { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public string RootType { get; init; } = string.Empty;

    public string DetailType { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public decimal TxDebit { get; init; }

    public decimal TxCredit { get; init; }

    public decimal Debit { get; init; }

    public decimal Credit { get; init; }

    public string? TaxComponentType { get; init; }

    public string? ControlRole { get; init; }

    public Guid? PartyId { get; init; }
}
