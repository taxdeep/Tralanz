namespace Modules.GL.JournalEntry;

public sealed class JournalEntryLifecycleWorkflow : IJournalEntryLifecycleWorkflow
{
    private readonly IJournalEntryLifecycleStore _store;

    public JournalEntryLifecycleWorkflow(IJournalEntryLifecycleStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<JournalEntryLifecycleResult> VoidAsync(
        Guid companyId,
        Guid journalEntryId,
        Guid userId,
        CancellationToken cancellationToken) =>
        _store.VoidAsync(companyId, journalEntryId, userId, cancellationToken);

    public Task<JournalEntryLifecycleResult> ReverseAsync(
        Guid companyId,
        Guid journalEntryId,
        Guid userId,
        CancellationToken cancellationToken) =>
        _store.ReverseAsync(companyId, journalEntryId, userId, cancellationToken);
}
