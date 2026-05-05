namespace Modules.GL.JournalEntry;

public interface IJournalEntryLifecycleStore
{
    Task<JournalEntryLifecycleResult> VoidAsync(
        CompanyId companyId,
        Guid journalEntryId,
        UserId userId,
        CancellationToken cancellationToken);

    Task<JournalEntryLifecycleResult> ReverseAsync(
        CompanyId companyId,
        Guid journalEntryId,
        UserId userId,
        CancellationToken cancellationToken);
}
