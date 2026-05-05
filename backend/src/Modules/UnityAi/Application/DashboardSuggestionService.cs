using System.Text.Json;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Deterministic suggestion generator. Reads <see cref="IReportUsageStatStore"/>,
/// maps frequently-used reports to candidate widget keys, and emits pending
/// suggestions for any candidate the user does not yet have on their dashboard
/// and does not already have a non-terminal suggestion for.
///
/// Thresholds (V1):
///   open_count_30d >= 3          OR
///   export_count >= 1            OR
///   drilldown_count >= 2
/// </summary>
public sealed class DashboardSuggestionService : IDashboardSuggestionService
{
    private const int OpenThreshold30d = 3;
    private const int ExportThreshold30d = 1;
    private const int DrilldownThreshold30d = 2;

    private static readonly IReadOnlyDictionary<string, string> ReportToWidget =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ar_aging"] = "ar_aging",
            ["ap_aging"] = "ap_aging",
            ["balance_sheet"] = "balance_sheet",
            ["income_statement"] = "income_statement",
            ["profit_loss"] = "profit_loss",
            ["trial_balance"] = "trial_balance",
            ["cash_flow"] = "cash_flow",
            ["sales_tax"] = "sales_tax_payable",
            ["bills_due"] = "bills_due",
            ["open_invoices"] = "open_invoices",
        };

    private readonly IReportUsageStatStore _reportStats;
    private readonly IDashboardUserWidgetStore _userWidgets;
    private readonly IDashboardWidgetSuggestionStore _suggestions;
    private readonly IAiJobRunStore _jobRuns;
    private readonly UnityAiFeatureFlagAccessor _flags;
    private readonly ILogger<DashboardSuggestionService> _logger;

    public DashboardSuggestionService(
        IReportUsageStatStore reportStats,
        IDashboardUserWidgetStore userWidgets,
        IDashboardWidgetSuggestionStore suggestions,
        IAiJobRunStore jobRuns,
        UnityAiFeatureFlagAccessor flags,
        ILogger<DashboardSuggestionService> logger)
    {
        _reportStats = reportStats;
        _userWidgets = userWidgets;
        _suggestions = suggestions;
        _jobRuns = jobRuns;
        _flags = flags;
        _logger = logger;
    }

    public async Task<DashboardSuggestionGenerationResult> GenerateAsync(
        CompanyId companyId,
        UserId? userId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        if (!_flags.DashboardRecommendationEnabled)
        {
            return new DashboardSuggestionGenerationResult(0, 0, 0);
        }

        var jobRunId = await _jobRuns.StartAsync(
            companyId,
            AiJobType.DashboardRecommendation,
            AiJobRunTriggerType.Manual,
            triggeredByUserId: userId,
            sourceWindowStart: windowStart,
            sourceWindowEnd: windowEnd,
            inputSummaryJson: null,
            cancellationToken).ConfigureAwait(false);

        var inserted = 0;
        var skippedActive = 0;
        var skippedAlreadySuggested = 0;

        try
        {
            // Use user-scoped stats when a user is supplied; otherwise the
            // company aggregate so company-wide dashboards still get suggestions.
            var scope = userId.HasValue ? UnitysearchScopeType.User : UnitysearchScopeType.Company;
            var stats = await _reportStats.GetForCompanyAsync(companyId, userId, scope, cancellationToken).ConfigureAwait(false);
            if (stats.Count == 0)
            {
                await _jobRuns.CompleteAsync(jobRunId, AiJobRunStatus.Succeeded, "{\"suggestions\":0}", null, null, cancellationToken).ConfigureAwait(false);
                return new DashboardSuggestionGenerationResult(0, 0, 0);
            }

            var existingWidgets = await _userWidgets.GetActiveAsync(companyId, userId, cancellationToken).ConfigureAwait(false);
            var existingWidgetKeys = new HashSet<string>(existingWidgets.Select(w => w.WidgetKey), StringComparer.OrdinalIgnoreCase);

            var candidateWidgetKeys = new List<(string ReportKey, string WidgetKey, ReportUsageStatRecord Stat)>();
            foreach (var stat in stats)
            {
                if (!ReportToWidget.TryGetValue(stat.ReportKey, out var widgetKey))
                {
                    continue;
                }

                if (!MeetsThreshold(stat))
                {
                    continue;
                }

                if (existingWidgetKeys.Contains(widgetKey))
                {
                    skippedActive++;
                    continue;
                }

                candidateWidgetKeys.Add((stat.ReportKey, widgetKey, stat));
            }

            if (candidateWidgetKeys.Count == 0)
            {
                await _jobRuns.CompleteAsync(jobRunId, AiJobRunStatus.Succeeded, "{\"suggestions\":0}", null, null, cancellationToken).ConfigureAwait(false);
                return new DashboardSuggestionGenerationResult(0, skippedActive, 0);
            }

            var widgetKeys = candidateWidgetKeys.Select(x => x.WidgetKey).Distinct().ToArray();
            var existingSuggestions = await _suggestions.GetExistingForWidgetKeysAsync(companyId, userId, widgetKeys, cancellationToken).ConfigureAwait(false);
            var blockingStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                DashboardSuggestionStatus.Pending,
                DashboardSuggestionStatus.Snoozed,
                DashboardSuggestionStatus.Accepted,
            };

            var blockedKeys = new HashSet<string>(
                existingSuggestions
                    .Where(s => blockingStatuses.Contains(s.Status))
                    .Select(s => s.WidgetKey),
                StringComparer.OrdinalIgnoreCase);

            var now = DateTimeOffset.UtcNow;
            foreach (var (reportKey, widgetKey, stat) in candidateWidgetKeys)
            {
                if (blockedKeys.Contains(widgetKey))
                {
                    skippedAlreadySuggested++;
                    continue;
                }

                var record = new DashboardWidgetSuggestionRecord(
                    Id: Guid.NewGuid(),
                    CompanyId: companyId,
                    UserId: userId,
                    WidgetKey: widgetKey,
                    Title: BuildTitle(widgetKey),
                    Reason: BuildReason(stat),
                    EvidenceJson: JsonSerializer.Serialize(new
                    {
                        report_key = reportKey,
                        open_count_30d = stat.OpenCount,
                        export_count_30d = stat.ExportCount,
                        drilldown_count_30d = stat.DrilldownCount,
                        last_opened_at = stat.LastOpenedAt,
                    }),
                    Confidence: 0.7m,
                    Source: DashboardSuggestionSource.System,
                    Status: DashboardSuggestionStatus.Pending,
                    JobRunId: jobRunId,
                    AcceptedAt: null,
                    DismissedAt: null,
                    SnoozedUntil: null,
                    CreatedAt: now,
                    UpdatedAt: now);

                await _suggestions.InsertAsync(record, cancellationToken).ConfigureAwait(false);
                inserted++;
            }

            await _jobRuns.CompleteAsync(
                jobRunId,
                AiJobRunStatus.Succeeded,
                JsonSerializer.Serialize(new { inserted, skippedActive, skippedAlreadySuggested }),
                null, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "dashboard suggestion generation failed (company={CompanyId})", companyId);
            await _jobRuns.CompleteAsync(jobRunId, AiJobRunStatus.Failed, null, ex.Message, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return new DashboardSuggestionGenerationResult(inserted, skippedActive, skippedAlreadySuggested);
    }

    private static bool MeetsThreshold(ReportUsageStatRecord stat) =>
        stat.OpenCount >= OpenThreshold30d ||
        stat.ExportCount >= ExportThreshold30d ||
        stat.DrilldownCount >= DrilldownThreshold30d;

    private static string BuildTitle(string widgetKey) =>
        $"Add {Humanize(widgetKey)} to dashboard";

    private static string BuildReason(ReportUsageStatRecord stat)
    {
        if (stat.OpenCount >= OpenThreshold30d)
        {
            return $"You opened {stat.ReportKey} {stat.OpenCount} times in the last 30 days.";
        }

        if (stat.ExportCount >= ExportThreshold30d)
        {
            return $"You exported {stat.ReportKey} {stat.ExportCount} time(s) in the last 30 days.";
        }

        return $"You drilled into {stat.ReportKey} {stat.DrilldownCount} time(s) in the last 30 days.";
    }

    private static string Humanize(string widgetKey) =>
        string.Join(' ', widgetKey.Split('_').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
}
