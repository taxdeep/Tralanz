using System.Net.Http.Json;
using Citus.Modules.UnitySearch.Application.Contracts;

namespace Web.Shell.Services;

public sealed class UnitySearchClient(HttpClient httpClient, ILogger<UnitySearchClient> logger)
{
    public async Task<UnitySearchResult> SearchAsync(
        Guid companyId,
        Guid? userId,
        string context,
        string searchText,
        int take,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/unity-search?companyId={companyId:D}&context={Uri.EscapeDataString(context)}&query={Uri.EscapeDataString(searchText)}&take={Math.Clamp(take, 1, 50)}";
        if (userId.HasValue)
        {
            requestUri += $"&userId={userId.Value:D}";
        }

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<UnitySearchResult>(cancellationToken)
                    ?? new UnitySearchResult
                    {
                        QueryText = searchText,
                        Context = context
                    };
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UnitySearch query failed for {Context}.", context);
        }

        return new UnitySearchResult
        {
            QueryText = searchText,
            Context = context
        };
    }

    public async Task<IReadOnlyList<UnitySearchRecentQueryRecord>> ListRecentQueriesAsync(
        Guid companyId,
        Guid userId,
        string context,
        int take,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/unity-search/recent?companyId={companyId:D}&context={Uri.EscapeDataString(context)}&userId={userId:D}&take={Math.Clamp(take, 1, 20)}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<IReadOnlyList<UnitySearchRecentQueryRecord>>(cancellationToken)
                    ?? Array.Empty<UnitySearchRecentQueryRecord>();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UnitySearch recent query lookup failed for {Context}.", context);
        }

        return Array.Empty<UnitySearchRecentQueryRecord>();
    }

    public async Task<IReadOnlyList<UnitySearchRecentSelectionRecord>> ListRecentSelectionsAsync(
        Guid companyId,
        Guid userId,
        string context,
        int take,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/unity-search/recent-selections?companyId={companyId:D}&context={Uri.EscapeDataString(context)}&userId={userId:D}&take={Math.Clamp(take, 1, 20)}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<IReadOnlyList<UnitySearchRecentSelectionRecord>>(cancellationToken)
                    ?? Array.Empty<UnitySearchRecentSelectionRecord>();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UnitySearch recent selection lookup failed for {Context}.", context);
        }

        return Array.Empty<UnitySearchRecentSelectionRecord>();
    }

    public async Task RecordClickAsync(
        Guid companyId,
        Guid userId,
        string context,
        string entityType,
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "accounting/unity-search/clicks",
                new
                {
                    CompanyId = companyId,
                    UserId = userId,
                    Context = context,
                    EntityType = entityType,
                    SourceId = sourceId
                },
                cancellationToken);

            _ = response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UnitySearch click record failed for {EntityType} {SourceId}.", entityType, sourceId);
        }
    }
}
