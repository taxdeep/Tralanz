using System.Net.Http.Json;
using Citus.Modules.UnitySearch.Application.Contracts;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnitySearch.Blazor;

public sealed class UnitySearchPickerService(HttpClient httpClient, ILogger<UnitySearchPickerService> logger)
{
    public async Task<IReadOnlyList<UnitySearchPickerOption>> SearchAsync(
        Guid companyId,
        Guid? userId,
        string context,
        string searchText,
        int take,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/unity-search?companyId={companyId:D}&context={Uri.EscapeDataString(context)}&query={Uri.EscapeDataString(searchText)}&take={Math.Clamp(take, 1, 20)}";
        if (userId.HasValue && userId.Value != Guid.Empty)
        {
            requestUri += $"&userId={userId.Value:D}";
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

            return payload.Groups
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
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UnitySearch picker query failed for {Context}.", context);
            return Array.Empty<UnitySearchPickerOption>();
        }
    }
}
