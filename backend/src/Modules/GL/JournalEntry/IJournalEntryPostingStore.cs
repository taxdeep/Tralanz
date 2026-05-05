namespace Modules.GL.JournalEntry;

public interface IJournalEntryPostingStore
{
    Task<JournalEntryPostResult> PostAsync(
        JournalEntryDraft draft,
        UserId userId,
        CancellationToken cancellationToken);
}
