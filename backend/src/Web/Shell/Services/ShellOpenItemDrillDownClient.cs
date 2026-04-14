using System.Net;
using System.Net.Http.Json;

namespace Web.Shell.Services;

public sealed class ShellOpenItemDrillDownClient(HttpClient httpClient, ILogger<ShellOpenItemDrillDownClient> logger)
{
    public async Task<ShellOpenItemDrillDownResponse?> GetAsync(
        Guid companyId,
        string openItemType,
        Guid openItemId,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildPath(openItemType, openItemId, out var requestPath))
        {
            logger.LogInformation("Unsupported open item drill-down type {OpenItemType}.", openItemType);
            return null;
        }

        var requestUri = $"{requestPath}?companyId={companyId:D}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ShellOpenItemDrillDownResponse>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load open item drill-down for {OpenItemType} {OpenItemId}.", openItemType, openItemId);
            return null;
        }
    }

    private static bool TryBuildPath(string? openItemType, Guid openItemId, out string path)
    {
        path = openItemType?.Trim().ToLowerInvariant() switch
        {
            "ar" => $"accounting/open-items/ar/{openItemId:D}",
            "ap" => $"accounting/open-items/ap/{openItemId:D}",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(path);
    }
}
