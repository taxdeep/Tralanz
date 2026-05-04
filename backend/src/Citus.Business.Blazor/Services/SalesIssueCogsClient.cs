using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the M3 Sales Issue → COGS bridge surface:
/// list workbench (<c>GET /accounting/sales-issues/cogs-status</c>) +
/// per-issue post trigger
/// (<c>POST /accounting/sales-issues/{id}/cogs/post</c>). Active company
/// resolves server-side via the BusinessSession header.
/// </summary>
public sealed class SalesIssueCogsClient(HttpClient httpClient, ILogger<SalesIssueCogsClient> logger)
{
    public async Task<IReadOnlyList<SalesIssueCogsStatusSummary>> ListAsync(
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/sales-issues/cogs-status?take={Math.Clamp(take, 1, 500)}";
            var rows = await httpClient.GetFromJsonAsync<SalesIssueCogsStatusSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<SalesIssueCogsStatusSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read sales-issue COGS status.");
            return Array.Empty<SalesIssueCogsStatusSummary>();
        }
    }

    public async Task<SalesIssueCogsPostOutcome> PostAsync(
        Guid salesIssueDocumentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/sales-issues/{salesIssueDocumentId:D}/cogs/post",
                content: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SalesIssueCogsPostOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<SalesIssueCogsPostResult>(cancellationToken);
            return new SalesIssueCogsPostOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to post COGS for sales-issue {SalesIssueId}.", salesIssueDocumentId);
            return new SalesIssueCogsPostOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return $"Request failed with status code {(int)response.StatusCode}.";
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString() ?? raw;
            }
        }
        catch (JsonException) { }
        return raw;
    }
}

public sealed record SalesIssueCogsStatusSummary(
    Guid SalesIssueDocumentId,
    DateOnly PostingDate,
    string? SourceDocumentNumber,
    decimal EstimatedCogsBase,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber);

public sealed record SalesIssueCogsPostResult(
    Guid SalesIssueDocumentId,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    bool AlreadyPosted,
    decimal TotalAmountBase);

public sealed record SalesIssueCogsPostOutcome(
    bool Succeeded,
    SalesIssueCogsPostResult? Saved,
    string? ErrorMessage);
