using System.Net;
using System.Net.Http.Json;
using Citus.Ui.Shared.Journal;

namespace Citus.Business.Blazor.Services;

public sealed class JournalEntryReviewClient(HttpClient httpClient, ILogger<JournalEntryReviewClient> logger)
{
    public async Task<IReadOnlyList<JournalEntryReviewListItemSummary>> GetRecentAsync(
        Guid companyId,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/journal-entries?companyId={companyId:D}&take={take}";

        try
        {
            return await httpClient.GetFromJsonAsync<JournalEntryReviewListItemSummary[]>(requestUri, cancellationToken)
                ?? Array.Empty<JournalEntryReviewListItemSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load recent journal entries.");
            return Array.Empty<JournalEntryReviewListItemSummary>();
        }
    }

    public async Task<JournalEntryReviewSummary?> GetAsync(
        Guid companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/journal-entries/{journalEntryId:D}?companyId={companyId:D}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Journal entry review is unavailable because {JournalEntryId} was not found.", journalEntryId);
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JournalEntryReviewSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load journal entry review for {JournalEntryId}.", journalEntryId);
            return null;
        }
    }

    public async Task<JournalEntryReviewListItemSummary?> FindBySourceAsync(
        Guid companyId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"accounting/journal-entries/by-source/{sourceType}/{sourceId:D}?companyId={companyId:D}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JournalEntryReviewListItemSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to locate journal entry for source {SourceType} {SourceId}.", sourceType, sourceId);
            return null;
        }
    }
}
