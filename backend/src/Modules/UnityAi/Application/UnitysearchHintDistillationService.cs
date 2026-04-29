using System.Text.Json;
using System.Text.Json.Serialization;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Citus.Modules.UnitySearch.Application.Contracts;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Public surface for the UnitySearch hint-distillation flow. Reads a
/// company's most-clicked entities, asks the AI gateway to suggest small
/// relevance boosts, and persists the suggestions into
/// <c>unitysearch_ranking_hints</c>. The deterministic ranking engine
/// then mixes those boosts (capped at 5 points) into its score formula
/// — AI is an enhancer, never the decider.
///
/// Single-company per call. Iterating over multiple companies is a job
/// for the caller (a hosted scheduler today, an admin trigger tomorrow).
/// </summary>
public interface IUnitysearchHintDistillationService
{
    Task<UnitysearchHintDistillationResult> DistillForCompanyAsync(
        Guid companyId,
        Guid? triggeredByUserId,
        string triggerType,
        CancellationToken cancellationToken);
}

public sealed record UnitysearchHintDistillationResult(
    Guid? JobRunId,
    int CandidateBuckets,
    int GatewayCalls,
    int HintsWritten,
    int SkippedReasonInsufficientActivity,
    int FailedGatewayCalls,
    string? OverallStatus,
    string? Note);

public sealed class UnitysearchHintDistillationService : IUnitysearchHintDistillationService
{
    /// <summary>
    /// Per-(context, entity_type) bucket: don't bother asking AI when there
    /// are too few candidates — the deterministic engine handles tiny
    /// candidate sets fine, and AI cost would dwarf any quality signal.
    /// </summary>
    private const int MinCandidatesPerBucket = 3;

    /// <summary>
    /// Total entities pulled per company per run. Buckets are sliced from
    /// this set after we have the rows. 200 is well under the validator's
    /// 200-hint cap, and the prompt itself caps suggestions at 8 per call.
    /// </summary>
    private const int CompanyTopN = 200;

    /// <summary>How long an AI hint stays active before re-distillation.</summary>
    private static readonly TimeSpan HintTtl = TimeSpan.FromDays(14);

    /// <summary>Max boost we will persist regardless of what the AI returns.</summary>
    private const decimal HintBoostCap = 3m;

    private readonly IUnityAiGateway _gateway;
    private readonly IUnitysearchUsageStatStore _usageStats;
    private readonly IUnitysearchRankingHintStore _hints;
    private readonly IUnitySearchProjectionStore _projection;
    private readonly IAiJobRunStore _jobRuns;
    private readonly UnityAiFeatureFlagAccessor _flags;
    private readonly ILogger<UnitysearchHintDistillationService> _logger;

    public UnitysearchHintDistillationService(
        IUnityAiGateway gateway,
        IUnitysearchUsageStatStore usageStats,
        IUnitysearchRankingHintStore hints,
        IUnitySearchProjectionStore projection,
        IAiJobRunStore jobRuns,
        UnityAiFeatureFlagAccessor flags,
        ILogger<UnitysearchHintDistillationService> logger)
    {
        _gateway = gateway;
        _usageStats = usageStats;
        _hints = hints;
        _projection = projection;
        _jobRuns = jobRuns;
        _flags = flags;
        _logger = logger;
    }

    public async Task<UnitysearchHintDistillationResult> DistillForCompanyAsync(
        Guid companyId,
        Guid? triggeredByUserId,
        string triggerType,
        CancellationToken cancellationToken)
    {
        if (!_flags.UnitysearchAiLearningEnabled)
        {
            return new UnitysearchHintDistillationResult(
                JobRunId: null,
                CandidateBuckets: 0,
                GatewayCalls: 0,
                HintsWritten: 0,
                SkippedReasonInsufficientActivity: 0,
                FailedGatewayCalls: 0,
                OverallStatus: AiJobRunStatus.Skipped,
                Note: "UNITYSEARCH_AI_LEARNING_ENABLED is off — flip the flag to run distillation.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var jobRunId = await _jobRuns.StartAsync(
            companyId: companyId,
            jobType: AiJobType.UnitysearchLearning,
            triggerType: triggerType,
            triggeredByUserId: triggeredByUserId,
            sourceWindowStart: startedAt - TimeSpan.FromDays(30),
            sourceWindowEnd: startedAt,
            inputSummaryJson: null,
            cancellationToken).ConfigureAwait(false);

        var top = await _usageStats.GetTopByCompanyScopeAsync(companyId, CompanyTopN, cancellationToken).ConfigureAwait(false);
        if (top.Count == 0)
        {
            await _jobRuns.CompleteAsync(jobRunId, AiJobRunStatus.Skipped, null, "No company-scope usage stats yet.", null, cancellationToken).ConfigureAwait(false);
            return new UnitysearchHintDistillationResult(
                JobRunId: jobRunId,
                CandidateBuckets: 0,
                GatewayCalls: 0,
                HintsWritten: 0,
                SkippedReasonInsufficientActivity: 1,
                FailedGatewayCalls: 0,
                OverallStatus: AiJobRunStatus.Skipped,
                Note: "Company has no usage stats yet — nothing to distill.");
        }

        // Slice into (context, entity_type) buckets. Each bucket becomes
        // one gateway call. Skipping buckets with too few candidates
        // keeps cost down and signal high.
        var buckets = top
            .GroupBy(r => (r.Context, r.EntityType))
            .Where(g => g.Count() >= MinCandidatesPerBucket)
            .Select(g => new { g.Key.Context, g.Key.EntityType, Rows = g.OrderByDescending(r => r.SelectCount30d).Take(30).ToArray() })
            .ToArray();

        if (buckets.Length == 0)
        {
            await _jobRuns.CompleteAsync(jobRunId, AiJobRunStatus.Skipped, null, "No bucket reached the minimum candidate count.", null, cancellationToken).ConfigureAwait(false);
            return new UnitysearchHintDistillationResult(
                JobRunId: jobRunId,
                CandidateBuckets: 0,
                GatewayCalls: 0,
                HintsWritten: 0,
                SkippedReasonInsufficientActivity: 1,
                FailedGatewayCalls: 0,
                OverallStatus: AiJobRunStatus.Skipped,
                Note: "Each bucket had fewer than the minimum candidate count — defer until activity grows.");
        }

        // Bulk-fetch display names for every (entity_type, entity_id) we
        // are about to ask the AI about. The LLM needs the human-readable
        // name to reason semantically — without it we'd be sending bare
        // UUIDs and click counts, which is roughly useless. Missing rows
        // are dropped from the bucket so we never send "(unknown)" to the
        // model and trick it into hallucinating.
        var nameLookupKeys = buckets
            .SelectMany(b => b.Rows.Select(r => (b.EntityType, r.EntityId)))
            .Distinct()
            .ToArray();
        var displayNames = await _projection.GetDisplayNamesAsync(companyId, nameLookupKeys, cancellationToken).ConfigureAwait(false);

        int gatewayCalls = 0;
        int hintsWritten = 0;
        int failedCalls = 0;
        var warningsPerBucket = new List<string>();

        var hintStatus = _flags.UnitysearchAiHintAutoApply
            ? UnitysearchHintStatus.Active
            : UnitysearchHintStatus.Pending;
        var expiresAt = DateTimeOffset.UtcNow.Add(HintTtl);
        var companyLabel = "company-" + companyId.ToString("N").AsSpan(0, 8).ToString();

        foreach (var bucket in buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var enriched = bucket.Rows
                .Select(r =>
                {
                    displayNames.TryGetValue((bucket.EntityType, r.EntityId), out var display);
                    return (Row: r, Display: display);
                })
                .Where(t => t.Display is not null && !string.IsNullOrWhiteSpace(t.Display.PrimaryText))
                .ToArray();

            if (enriched.Length < MinCandidatesPerBucket)
            {
                warningsPerBucket.Add($"{bucket.Context}/{bucket.EntityType}: dropped to {enriched.Length} candidate(s) after display-name lookup; skipping.");
                continue;
            }

            var input = new DistillationInput(
                CompanyLabel: companyLabel,
                Context: string.IsNullOrWhiteSpace(bucket.Context) ? "global" : bucket.Context,
                EntityType: bucket.EntityType,
                Candidates: enriched.Select(t => new DistillationCandidate(
                    Id: t.Row.EntityId.ToString("D"),
                    DisplayName: t.Display!.PrimaryText,
                    DisplaySecondary: string.IsNullOrWhiteSpace(t.Display.SecondaryText) ? null : t.Display.SecondaryText,
                    SelectCount30d: t.Row.SelectCount30d,
                    LastQuery: t.Row.LastQuery)).ToArray());

            var request = new UnityAiTaskRequest<DistillationInput>(
                TaskType: UnityAiPromptRegistry.UnitysearchRerankV1,
                Input: input,
                Context: new UnityAiInvocationContext(
                    CompanyId: companyId,
                    UserId: triggeredByUserId,
                    JobRunId: jobRunId,
                    ScopeLabel: $"{bucket.Context}/{bucket.EntityType}"),
                PromptVersion: null,
                TimeoutMs: 60_000);

            UnityAiTaskResult<DistillationOutput> result;
            try
            {
                result = await _gateway.RunStructuredTaskAsync<DistillationInput, DistillationOutput>(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failedCalls++;
                warningsPerBucket.Add($"{bucket.Context}/{bucket.EntityType}: gateway threw {ex.GetType().Name}");
                _logger.LogWarning(ex,
                    "UnitySearch hint distillation: gateway threw for company {CompanyId} bucket {Context}/{EntityType}",
                    companyId, bucket.Context, bucket.EntityType);
                continue;
            }

            gatewayCalls++;

            if (result.Outcome != UnityAiTaskOutcome.Succeeded)
            {
                if (result.Outcome != UnityAiTaskOutcome.Disabled && result.Outcome != UnityAiTaskOutcome.Skipped)
                {
                    failedCalls++;
                }
                warningsPerBucket.Add($"{bucket.Context}/{bucket.EntityType}: {result.Outcome} ({result.ErrorMessage ?? "no detail"})");
                continue;
            }

            if (result.Output?.Hints is null || result.Output.Hints.Length == 0)
            {
                continue;
            }

            // Build a lookup of valid candidate IDs so a hallucinated
            // entity_id from the LLM gets dropped silently rather than
            // poisoning the hint store.
            var candidateIds = bucket.Rows.Select(r => r.EntityId).ToHashSet();

            foreach (var hint in result.Output.Hints)
            {
                if (!Guid.TryParse(hint.TargetEntityId, out var targetId)) continue;
                if (!candidateIds.Contains(targetId)) continue;

                var clampedBoost = Math.Clamp(hint.Boost, 0m, HintBoostCap);
                var clampedConfidence = Math.Clamp(hint.Confidence, 0m, 1m);

                await _hints.UpsertAsync(new UnitysearchRankingHintRecord(
                    Id: Guid.NewGuid(),
                    CompanyId: companyId,
                    UserId: null,
                    Context: input.Context,
                    EntityType: bucket.EntityType,
                    EntityId: targetId,
                    BoostScore: clampedBoost,
                    Confidence: clampedConfidence,
                    Reason: TruncateReason(hint.Reason),
                    Source: UnitysearchHintSource.Ai,
                    Status: hintStatus,
                    ValidationStatus: UnitysearchHintValidationStatus.Unvalidated,
                    ExpiresAt: expiresAt), cancellationToken).ConfigureAwait(false);

                hintsWritten++;
            }
        }

        var overall = failedCalls == 0
            ? AiJobRunStatus.Succeeded
            : (hintsWritten > 0 ? AiJobRunStatus.Partial : AiJobRunStatus.Failed);

        var summaryJson = JsonSerializer.Serialize(new
        {
            buckets = buckets.Length,
            gateway_calls = gatewayCalls,
            hints_written = hintsWritten,
            failed_calls = failedCalls,
            hint_status = hintStatus,
        });

        var warnings = warningsPerBucket.Count == 0
            ? null
            : JsonSerializer.Serialize(new { warnings = warningsPerBucket.ToArray() });

        await _jobRuns.CompleteAsync(jobRunId, overall, summaryJson, null, warnings, cancellationToken).ConfigureAwait(false);

        return new UnitysearchHintDistillationResult(
            JobRunId: jobRunId,
            CandidateBuckets: buckets.Length,
            GatewayCalls: gatewayCalls,
            HintsWritten: hintsWritten,
            SkippedReasonInsufficientActivity: 0,
            FailedGatewayCalls: failedCalls,
            OverallStatus: overall,
            Note: warnings is null ? null : $"{failedCalls} bucket(s) failed; see job warnings.");
    }

    private static string TruncateReason(string reason)
    {
        const int max = 200;
        var t = reason.Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    /// <summary>
    /// Input shape sent to the gateway. Field names use snake_case to
    /// match the prompt copy and the validator's expected output keys —
    /// keeps the LLM's view consistent across input + output.
    /// </summary>
    private sealed record DistillationInput(
        [property: JsonPropertyName("company_label")] string CompanyLabel,
        [property: JsonPropertyName("context")] string Context,
        [property: JsonPropertyName("entity_type")] string EntityType,
        [property: JsonPropertyName("candidates")] DistillationCandidate[] Candidates);

    private sealed record DistillationCandidate(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("display_secondary")] string? DisplaySecondary,
        [property: JsonPropertyName("select_count_30d")] int SelectCount30d,
        [property: JsonPropertyName("last_query")] string? LastQuery);

    private sealed record DistillationOutput(
        [property: JsonPropertyName("hints")] DistillationHint[] Hints);

    private sealed record DistillationHint(
        [property: JsonPropertyName("target_entity_id")] string TargetEntityId,
        [property: JsonPropertyName("boost")] decimal Boost,
        [property: JsonPropertyName("confidence")] decimal Confidence,
        [property: JsonPropertyName("reason")] string Reason);
}
