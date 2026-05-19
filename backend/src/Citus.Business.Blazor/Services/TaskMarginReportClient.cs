using System.Net.Http.Json;
using Citus.Modules.Tasks.Domain.Shared.Reports;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the margin report endpoint added in Batch 10
/// (<c>GET /accounting/tasks/reports/margin</c>). Like the rest of the
/// read-side clients, this degrades to an empty result on failure so
/// the page can render a clean empty state instead of crashing.
/// </summary>
public sealed class TaskMarginReportClient(HttpClient httpClient, ILogger<TaskMarginReportClient> logger)
{
    public async Task<TaskMarginReportResult> GetReportAsync(
        TaskMarginReportMode mode,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        Guid? customerId = null,
        string? assigneeId = null,
        int take = 200,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>
            {
                $"mode={Uri.EscapeDataString(mode.ToString().ToLowerInvariant())}",
                $"take={take}",
                $"skip={skip}",
            };
            if (fromDate.HasValue) query.Add($"from={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) query.Add($"to={toDate.Value:yyyy-MM-dd}");
            if (customerId.HasValue) query.Add($"customerId={customerId.Value:D}");
            if (!string.IsNullOrWhiteSpace(assigneeId)) query.Add($"assigneeId={Uri.EscapeDataString(assigneeId.Trim())}");

            var url = "accounting/tasks/reports/margin?" + string.Join("&", query);
            var result = await httpClient.GetFromJsonAsync<TaskMarginReportResult>(url, cancellationToken);
            return result ?? Empty(mode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load task margin report.");
            return Empty(mode);
        }
    }

    private static TaskMarginReportResult Empty(TaskMarginReportMode mode) => new()
    {
        Mode = mode,
        Rows = Array.Empty<TaskMarginRow>(),
        Summary = new TaskMarginSummary
        {
            TaskCount = 0,
            TotalBillableValue = 0m,
            TotalDirectCost = 0m,
            TotalGrossMargin = 0m,
            WeightedGrossMarginPercent = null,
        },
    };
}
