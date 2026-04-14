namespace Modules.GL.JournalEntry;

public interface IJournalEntryLifecycleStore
{
    Task<JournalEntryLifecycleResult> VoidAsync(
        Guid companyId,
        Guid journalEntryId,
        Guid userId,
        CancellationToken cancellationToken);

    Task<JournalEntryLifecycleResult> ReverseAsync(
        Guid companyId,
        Guid journalEntryId,
        Guid userId,
        CancellationToken cancellationToken);
}
