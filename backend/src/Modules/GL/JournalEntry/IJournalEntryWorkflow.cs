namespace Modules.GL.JournalEntry;

public interface IJournalEntryWorkflow
{
    Task<IReadOnlyList<JournalEntryAccountOption>> LoadAccountOptionsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<JournalEntryDraftSaveResult> SaveDraftAsync(
        JournalEntryDraft draft,
        UserId userId,
        CancellationToken cancellationToken);

    Task<JournalEntryPostResult> PostDraftAsync(
        JournalEntryDraft draft,
        UserId userId,
        CancellationToken cancellationToken);
}
