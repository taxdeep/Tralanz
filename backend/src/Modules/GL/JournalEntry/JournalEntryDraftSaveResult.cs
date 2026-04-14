namespace Modules.GL.JournalEntry;

public sealed record class JournalEntryDraftSaveResult(
    Guid DocumentId,
    string DocumentNumber,
    string Status);
