using System.Net.Http.Json;
using System.Text.Json;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Microsoft.Extensions.Logging;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the Task module endpoints (<c>/accounting/tasks/...</c>
/// added in Batch 5). Read paths degrade to empty / null on failure so
/// pages can render a clean empty state; write paths surface an
/// outcome record with the API's error message attached.
/// </summary>
public sealed class TaskClient(HttpClient httpClient, ILogger<TaskClient> logger)
{
    public async Task<IReadOnlyList<TaskSummary>> ListAsync(
        TaskStatus? status = null,
        Guid? customerId = null,
        int take = 50,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string> { $"take={take}", $"skip={skip}" };
            if (status.HasValue) query.Add($"status={Uri.EscapeDataString(((int)status.Value).ToString())}");
            if (customerId.HasValue) query.Add($"customerId={customerId.Value:D}");

            var url = "accounting/tasks?" + string.Join("&", query);
            var rows = await httpClient.GetFromJsonAsync<TaskSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<TaskSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load tasks.");
            return Array.Empty<TaskSummary>();
        }
    }

    public async Task<TaskRecord?> GetAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync($"accounting/tasks/{taskId:D}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<TaskRecord>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load task {TaskId}.", taskId);
            return null;
        }
    }

    public Task<TaskMutationOutcome> CreateAsync(
        TaskCreateRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "accounting/tasks", request, cancellationToken);

    public Task<TaskMutationOutcome> UpdateAsync(
        Guid taskId,
        TaskUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"accounting/tasks/{taskId:D}", request, cancellationToken);

    public Task<TaskMutationOutcome> AddLineAsync(
        Guid taskId,
        TaskLineUpsertRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"accounting/tasks/{taskId:D}/lines", request, cancellationToken);

    public async Task<TaskMutationOutcome> RemoveLineAsync(
        Guid taskId,
        Guid lineId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.DeleteAsync(
                $"accounting/tasks/{taskId:D}/lines/{lineId:D}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new TaskMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            return new TaskMutationOutcome(true, await response.Content.ReadFromJsonAsync<TaskRecord>(cancellationToken), null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to remove task line {LineId}.", lineId);
            return new TaskMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    public Task<TaskMutationOutcome> CompleteAsync(
        Guid taskId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"accounting/tasks/{taskId:D}/complete", new { Reason = reason }, cancellationToken);

    public Task<TaskMutationOutcome> CancelAsync(
        Guid taskId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"accounting/tasks/{taskId:D}/cancel", new { Reason = reason }, cancellationToken);

    /// <summary>
    /// Batch resolves a set of task ids to "TSK-000123 - Title" display
    /// labels. Empty input returns an empty dictionary without a HTTP
    /// roundtrip. Used by edit pages (bill / expense / credit-memo) to
    /// render the per-line TaskPicker with the real label on first
    /// render rather than a placeholder short-GUID.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> LookupDisplayAsync(
        IEnumerable<Guid> taskIds,
        CancellationToken cancellationToken = default)
    {
        var distinct = taskIds
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (distinct.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        try
        {
            // Repeated query param binding: ASP.NET binds ?ids=g1&ids=g2&...
            // into Guid[]. Same shape on both sides keeps the wire simple.
            var query = string.Join('&', distinct.Select(id => $"ids={id:D}"));
            var rows = await httpClient.GetFromJsonAsync<TaskDisplayLookupDto[]>(
                $"accounting/tasks/lookup?{query}",
                cancellationToken);
            if (rows is null) return new Dictionary<Guid, string>();
            return rows.ToDictionary(
                static r => r.TaskId,
                static r => string.IsNullOrWhiteSpace(r.Title) ? r.TaskNo : $"{r.TaskNo} - {r.Title}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to batch-resolve task display labels.");
            return new Dictionary<Guid, string>();
        }
    }

    /// <summary>Wire shape of one row from <c>GET /accounting/tasks/lookup</c>.</summary>
    private sealed record TaskDisplayLookupDto(Guid TaskId, string TaskNo, string Title);

    private async Task<TaskMutationOutcome> SendAsync(
        HttpMethod method,
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(payload),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new TaskMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }

            // Successful state-change endpoints return either TaskRecord
            // or no body (204). Be defensive about the empty case.
            if (response.Content.Headers.ContentLength is 0)
            {
                return new TaskMutationOutcome(true, null, null);
            }
            var saved = await response.Content.ReadFromJsonAsync<TaskRecord>(cancellationToken);
            return new TaskMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task API call to {Path} failed.", path);
            return new TaskMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return $"Request failed with status code {(int)response.StatusCode}.";
        }
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? raw;
            }
        }
        catch (JsonException) { }
        return raw;
    }
}

public sealed record TaskMutationOutcome(bool Succeeded, TaskRecord? Saved, string? ErrorMessage);
