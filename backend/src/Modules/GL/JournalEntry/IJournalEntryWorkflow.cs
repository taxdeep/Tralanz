namespace Modules.GL.JournalEntry;

public interface IJournalEntryWorkflow
{
    Task<IReadOnlyList<JournalEntryAccountOption>> LoadAccountOptionsAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<JournalEntryDraftSaveResult> SaveDraftAsync(
        JournalEntryDraft draft,
        Guid userId,
        CancellationToken cancellationToken);

    Task<JournalEntryPostResult> PostDraftAsync(
        JournalEntryDraft draft,
        Guid userId,
        CancellationToken cancellationToken);
}
