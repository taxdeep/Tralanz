namespace Modules.GL.JournalEntry;

public interface IJournalEntryReviewStore
{
    Task<JournalEntryReview?> GetAsync(
        Guid companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken);
}
