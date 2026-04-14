namespace Modules.GL.JournalEntry;

public interface IJournalEntryDraftStore
{
    Task<JournalEntryDraftSaveResult> SaveAsync(
        JournalEntryDraft draft,
        Guid userId,
        CancellationToken cancellationToken);
}
