namespace Engines.Numbering.JournalEntry;

public interface IJournalEntryNumberLookup
{
    Task<string> GetNextDisplayNumberAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<string> ReserveNextDisplayNumberAsync(
        Guid companyId,
        CancellationToken cancellationToken);
}
