namespace Engines.Numbering.JournalEntry;

public interface IJournalEntryNumberLookup
{
    Task<string> GetNextDisplayNumberAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<string> ReserveNextDisplayNumberAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);
}
