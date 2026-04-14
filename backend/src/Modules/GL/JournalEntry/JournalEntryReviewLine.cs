namespace Modules.GL.JournalEntry;

public sealed class JournalEntryReviewLine
{
    public required Guid Id { get; init; }

    public required int LineNumber { get; init; }

    public required Guid AccountId { get; init; }

    public required string AccountCode { get; init; }

    public required string AccountName { get; init; }

    public required string RootType { get; init; }

    public required string DetailType { get; init; }

    public required string Description { get; init; }

    public required decimal TransactionDebit { get; init; }

    public required decimal TransactionCredit { get; init; }

    public required decimal Debit { get; init; }

    public required decimal Credit { get; init; }

    public string? TaxComponentType { get; init; }

    public string? ControlRole { get; init; }

    public Guid? PartyId { get; init; }

    public string? AccountSystemRole { get; init; }

    public string? AccountSystemKey { get; init; }

    public bool IsRealizedFxLine =>
        string.Equals(AccountSystemRole, "realized_fx_gain", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(AccountSystemRole, "realized_fx_loss", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(AccountSystemKey, "fx_gain_realized", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(AccountSystemKey, "fx_loss_realized", StringComparison.OrdinalIgnoreCase);

    public string DisplayDescription =>
        string.IsNullOrWhiteSpace(Description) ? "No description" : Description;
}
