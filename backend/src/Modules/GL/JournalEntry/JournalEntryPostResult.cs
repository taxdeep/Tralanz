namespace Modules.GL.JournalEntry;

public sealed record class JournalEntryPostResult(
    Guid DocumentId,
    string DocumentNumber,
    Guid JournalEntryId,
    string JournalDisplayNumber);
