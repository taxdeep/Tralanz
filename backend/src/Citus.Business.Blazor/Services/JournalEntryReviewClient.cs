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

    /// <summary>
    /// Void a posted journal entry. The lifecycle workflow inserts a
    /// compensating reverse-side entry and marks the original as voided —
    /// nothing is deleted, so the audit trail stays intact. Returns an
    /// outcome with <c>Succeeded=false</c> + a human-readable message
    /// on failure so the page can surface it without parsing exceptions.
    /// </summary>
    public async Task<JournalEntryLifecycleOutcome> VoidAsync(
        Guid journalEntryId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/journal-entries/{journalEntryId:D}/void",
                content: null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<LifecycleErrorBody>(cancellationToken);
                return new JournalEntryLifecycleOutcome(
                    Succeeded: false,
                    Message: error?.Message ?? $"Could not void the journal entry (HTTP {(int)response.StatusCode}).",
                    Result: null);
            }

            var body = await response.Content.ReadFromJsonAsync<JournalEntryLifecyclePayload>(cancellationToken);
            return new JournalEntryLifecycleOutcome(
                Succeeded: true,
                Message: body is null
                    ? "Journal entry voided."
                    : $"Voided. Compensation entry {body.CompensationDisplayNumber} created.",
                Result: body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Void journal entry call failed for {JournalEntryId}.", journalEntryId);
            return new JournalEntryLifecycleOutcome(
                Succeeded: false,
                Message: "Could not reach the server. Please retry.",
                Result: null);
        }
    }

    /// <summary>
    /// Peek at the next journal display number the system will assign on
    /// save. Returns null on transport / parse failure — callers should fall
    /// back to a "Auto" placeholder so the form stays usable when offline.
    /// PEEK only — the actual number is reserved at post time, so concurrent
    /// operators may end up with a different value than the preview shown.
    /// </summary>
    public async Task<string?> GetNextDisplayNumberAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await httpClient.GetFromJsonAsync<NextDisplayNumberResponse>(
                "accounting/journal-entries/next-number",
                cancellationToken);
            return payload?.DisplayNumber;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to peek the next journal display number.");
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

    private sealed record NextDisplayNumberResponse(string DisplayNumber);

    private sealed record LifecycleErrorBody(string? Message);
}

public sealed record JournalEntryLifecyclePayload(
    Guid OriginalJournalEntryId,
    string OriginalDisplayNumber,
    string OriginalStatus,
    DateTimeOffset LifecycleAt,
    Guid CompensationJournalEntryId,
    string CompensationDisplayNumber,
    string CompensationSourceType);

public sealed record JournalEntryLifecycleOutcome(
    bool Succeeded,
    string Message,
    JournalEntryLifecyclePayload? Result);
