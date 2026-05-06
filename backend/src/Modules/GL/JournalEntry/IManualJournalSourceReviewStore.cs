namespace Modules.GL.JournalEntry;

public interface IManualJournalSourceReviewStore
{
    Task<ManualJournalSourceReview?> GetAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}
