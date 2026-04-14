namespace Modules.GL.JournalEntry;

public sealed class ManualJournalSourceReviewLine
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

    public string DisplayDescription =>
        string.IsNullOrWhiteSpace(Description) ? "No description" : Description;
}
