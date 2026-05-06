namespace Modules.GL.JournalEntry;

public interface IJournalEntryDraftStore
{
    Task<JournalEntryDraftSaveResult> SaveAsync(
        JournalEntryDraft draft,
        UserId userId,
        CancellationToken cancellationToken);
}
