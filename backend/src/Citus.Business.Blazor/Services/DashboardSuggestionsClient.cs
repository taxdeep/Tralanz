using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// H17: read-only client for the existing
/// <c>GET /accounting/dashboard/suggestions</c> endpoint
/// (DashboardSuggestionService). Used by the operator dashboard to
/// render the AI-generated suggestion cards. Read-path only — the
/// Accept / Dismiss / Snooze endpoints exist but H17 ships the
/// suggestions as a glanceable list without inline actions.
/// </summary>
public sealed class DashboardSuggestionsClient(HttpClient httpClient, ILogger<DashboardSuggestionsClient> logger)
{
    public async Task<IReadOnlyList<DashboardSuggestionItem>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        // Default status filter ('proposed') matches the suggestion
        // store's "freshly generated, not yet acted on" set.
        const string requestUri = "accounting/dashboard/suggestions?status=proposed";
        try
        {
            return await httpClient.GetFromJsonAsync<DashboardSuggestionItem[]>(requestUri, cancellationToken)
                ?? Array.Empty<DashboardSuggestionItem>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load dashboard AI suggestions.");
            return Array.Empty<DashboardSuggestionItem>();
        }
    }
}

/// <summary>
/// Slim projection of <c>DashboardWidgetSuggestionRecord</c> (the
/// canonical record lives in Citus.Modules.UnityAi.Application.
/// Contracts). Keeping a separate DTO here avoids dragging UnityAi
/// types into the Blazor host for what is otherwise a small read.
/// </summary>
public sealed record class DashboardSuggestionItem(
    Guid Id,
    string WidgetKey,
    string Title,
    string Reason,
    decimal Confidence,
    string Status,
    DateTimeOffset CreatedAt);
