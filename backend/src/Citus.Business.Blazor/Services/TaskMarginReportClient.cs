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
        string baseCurrency,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        Guid? customerId = null,
        string? assigneeId = null,
        int take = 200,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        var resolvedBase = string.IsNullOrWhiteSpace(baseCurrency)
            ? "USD"
            : baseCurrency.Trim().ToUpperInvariant();

        try
        {
            var query = new List<string>
            {
                $"mode={Uri.EscapeDataString(mode.ToString().ToLowerInvariant())}",
                $"baseCurrency={Uri.EscapeDataString(resolvedBase)}",
                $"take={take}",
                $"skip={skip}",
            };
            if (fromDate.HasValue) query.Add($"from={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) query.Add($"to={toDate.Value:yyyy-MM-dd}");
            if (customerId.HasValue) query.Add($"customerId={customerId.Value:D}");
            if (!string.IsNullOrWhiteSpace(assigneeId)) query.Add($"assigneeId={Uri.EscapeDataString(assigneeId.Trim())}");

            var url = "accounting/tasks/reports/margin?" + string.Join("&", query);
            var result = await httpClient.GetFromJsonAsync<TaskMarginReportResult>(url, cancellationToken);
            return result ?? Empty(mode, resolvedBase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load task margin report.");
            return Empty(mode, resolvedBase);
        }
    }

    private static TaskMarginReportResult Empty(TaskMarginReportMode mode, string baseCurrency) => new()
    {
        Mode = mode,
        Rows = Array.Empty<TaskMarginRow>(),
        Summary = new TaskMarginSummary
        {
            TaskCount = 0,
            TotalBillableValue = 0m,
            TotalBilledValue = 0m,
            TotalUnbilledValue = 0m,
            TotalDirectCost = 0m,
            TotalGrossMargin = 0m,
            WeightedGrossMarginPercent = null,
            BaseCurrencyCode = baseCurrency,
            TotalBillableValueBase = 0m,
            TotalDirectCostBase = 0m,
            TotalGrossMarginBase = 0m,
            WeightedGrossMarginPercentBase = null,
            UnresolvedFxCount = 0,
        },
    };
}
