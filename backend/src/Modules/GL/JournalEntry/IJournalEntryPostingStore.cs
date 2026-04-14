namespace Modules.GL.JournalEntry;

public interface IJournalEntryPostingStore
{
    Task<JournalEntryPostResult> PostAsync(
        JournalEntryDraft draft,
        Guid userId,
        CancellationToken cancellationToken);
}
