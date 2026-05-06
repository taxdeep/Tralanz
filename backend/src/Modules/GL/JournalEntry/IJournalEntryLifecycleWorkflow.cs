namespace Modules.GL.JournalEntry;

public interface IJournalEntryLifecycleWorkflow
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
