using System.Net.Http.Json;
using Citus.Modules.UnitySearch.Application.Contracts;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnitySearch.Blazor;

public sealed class UnitySearchPickerService(HttpClient httpClient, ILogger<UnitySearchPickerService> logger)
{
    public async Task<IReadOnlyList<UnitySearchPickerOption>> SearchAsync(
        CompanyId companyId,
        UserId? userId,
        string context,
        string searchText,
        int take,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/unity-search?companyId={companyId}&context={Uri.EscapeDataString(context)}&query={Uri.EscapeDataString(searchText)}&take={Math.Clamp(take, 1, 20)}";
        if (userId.HasValue)
        {
            requestUri += $"&userId={userId.Value}";
        }

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<UnitySearchPickerOption>();
            }

            var payload = await response.Content.ReadFromJsonAsync<UnitySearchResult>(cancellationToken);
            if (payload is null)
            {
                return Array.Empty<UnitySearchPickerOption>();
            }

            var groupedOptions = payload.Groups
                .SelectMany(static group => group.Items)
                .Select(static item => new UnitySearchPickerOption
                {
                    SourceId = item.SourceId,
                    EntityType = item.EntityType,
                    PrimaryText = item.PrimaryText,
                    SecondaryText = item.SecondaryText,
                    DisplayText = string.IsNullOrWhiteSpace(item.SecondaryText)
                        ? item.PrimaryText
                        : $"{item.PrimaryText} - {item.SecondaryText}",
                    NavigationHref = item.NavigationHref
                })
                .ToArray();

            if (groupedOptions.Length > 0)
            {
                return groupedOptions;
            }

            return payload.RecentSelections
                .Select(static item => new UnitySearchPickerOption
                {
                    SourceId = item.SourceId,
                    EntityType = item.EntityType,
                    PrimaryText = item.PrimaryText,
                    SecondaryText = item.SecondaryText,
                    DisplayText = string.IsNullOrWhiteSpace(item.SecondaryText)
                        ? item.PrimaryText
                        : $"{item.PrimaryText} - {item.SecondaryText}",
                    NavigationHref = item.NavigationHref
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UnitySearch picker query failed for {Context}.", context);
            return Array.Empty<UnitySearchPickerOption>();
        }
    }

    /// <summary>
    /// Fire-and-forget usage event for the unityAI learning loop. Failures
    /// are swallowed and logged at warn level — search UX must never break
    /// because tracking failed.
    ///
    /// The Accounting API enforces company isolation via the
    /// X-Citus-Business-* headers added by <c>BusinessSessionHeaderHandler</c>.
    /// Callers therefore do not need to forward credentials in the body.
    /// </summary>
    public async Task RecordUsageAsync(
        UnitysearchUsageEvent payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "accounting/unitysearch/usage",
                payload,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "UnitySearch usage tracking returned non-success status {StatusCode} for context {Context} event {EventType}.",
                    response.StatusCode, payload.Context, payload.EventType);
            }
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — swallow.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "UnitySearch usage tracking failed for context {Context} event {EventType}.",
                payload.Context, payload.EventType);
        }
    }
}

/// <summary>
/// Request payload for the Accounting API's <c>POST /accounting/unitysearch/usage</c>
/// endpoint. Property names match the server-side
/// <c>UnitysearchUsageHttpRequest</c> shape exactly so default System.Text.Json
/// PascalCase serialization round-trips cleanly.
/// </summary>
public sealed record UnitysearchUsageEvent
{
    public CompanyId CompanyId { get; init; }
    public string? SessionId { get; init; }
    public string Context { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string? Query { get; init; }
    public string EventType { get; init; } = string.Empty;
    public Guid? SelectedEntityId { get; init; }
    public int? RankPosition { get; init; }
    public int? ResultCount { get; init; }
    public string? SourceRoute { get; init; }
    public string? AnchorContext { get; init; }
    public string? AnchorEntityType { get; init; }
    public Guid? AnchorEntityId { get; init; }
    public string? MetadataJson { get; init; }
}
