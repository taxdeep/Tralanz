namespace Modules.GL.JournalEntry;

public interface IJournalEntryAccountCatalog
{
    Task<IReadOnlyList<JournalEntryAccountOption>> ListManualPostingAccountsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);
}
