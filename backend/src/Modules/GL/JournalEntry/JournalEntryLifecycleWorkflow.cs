namespace Modules.GL.JournalEntry;

public sealed class JournalEntryLifecycleWorkflow : IJournalEntryLifecycleWorkflow
{
    private readonly IJournalEntryLifecycleStore _store;

    public JournalEntryLifecycleWorkflow(IJournalEntryLifecycleStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<JournalEntryLifecycleResult> VoidAsync(
        CompanyId companyId,
        Guid journalEntryId,
        UserId userId,
        CancellationToken cancellationToken) =>
        _store.VoidAsync(companyId, journalEntryId, userId, cancellationToken);

    public Task<JournalEntryLifecycleResult> ReverseAsync(
        CompanyId companyId,
        Guid journalEntryId,
        UserId userId,
        CancellationToken cancellationToken) =>
        _store.ReverseAsync(companyId, journalEntryId, userId, cancellationToken);
}

public sealed class JournalEntryLifecycleException : InvalidOperationException
{
    public JournalEntryLifecycleException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
