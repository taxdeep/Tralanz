namespace Modules.GL.JournalEntry;

public interface IManualJournalSourceReviewStore
{
    Task<ManualJournalSourceReview?> GetAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}
