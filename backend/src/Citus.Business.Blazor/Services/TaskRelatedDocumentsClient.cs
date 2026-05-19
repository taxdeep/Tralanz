using System.Net.Http.Json;
using Citus.Modules.Tasks.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-task related-documents rollup
/// (<c>GET /accounting/tasks/{id}/related-documents</c>). Same
/// degrade-to-empty pattern as the rest of the read-side clients so
/// TaskDetailPage can render a clean empty state on any failure.
/// </summary>
public sealed class TaskRelatedDocumentsClient(HttpClient httpClient, ILogger<TaskRelatedDocumentsClient> logger)
{
    public async Task<IReadOnlyList<TaskRelatedDocument>> ListAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await httpClient.GetFromJsonAsync<TaskRelatedDocument[]>(
                $"accounting/tasks/{taskId:D}/related-documents",
                cancellationToken);
            return rows ?? Array.Empty<TaskRelatedDocument>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load related documents for task {TaskId}.", taskId);
            return Array.Empty<TaskRelatedDocument>();
        }
    }
}
