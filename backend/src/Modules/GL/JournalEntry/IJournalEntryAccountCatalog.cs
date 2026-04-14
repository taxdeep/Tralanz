namespace Modules.GL.JournalEntry;

public interface IJournalEntryAccountCatalog
{
    Task<IReadOnlyList<JournalEntryAccountOption>> ListManualPostingAccountsAsync(
        Guid companyId,
        CancellationToken cancellationToken);
}
