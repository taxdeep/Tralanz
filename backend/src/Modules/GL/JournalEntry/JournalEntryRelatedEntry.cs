namespace Modules.GL.JournalEntry;

public sealed record class JournalEntryRelatedEntry(
    Guid Id,
    string DisplayNumber,
    string Status,
    string SourceType,
    DateTimeOffset? PostedAt)
{
    public string SourceTypeLabel =>
        SourceType switch
        {
            "manual_journal" => "Original manual journal",
            "manual_journal_void" => "Void compensation",
            "manual_journal_reversal" => "Reversal compensation",
            _ => SourceType.Replace('_', ' ')
        };
}
