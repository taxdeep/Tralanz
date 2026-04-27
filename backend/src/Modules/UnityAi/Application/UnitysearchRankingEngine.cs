using System.Globalization;
using System.Text.Json;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Deterministic ranking. No external AI call. Score components:
///
///   final = text_match + user_freq + company_freq + recency
///         + pair + alias + ai_hint + status − penalty
///
/// Decision traces are written only when the trace flag is on AND the
/// per-call sampling roll succeeds, so live search stays fast.
/// </summary>
public sealed class UnitysearchRankingEngine : IUnitysearchRankingEngine
{
    // Score caps — keep AI hint capped well below text-match so AI cannot
    // dominate scope validity / exact match.
    private const decimal UserFrequencyCap = 30m;
    private const decimal CompanyFrequencyCap = 20m;
    private const decimal PairCap = 25m;
    private const decimal AiHintCap = 5m;
    private const decimal SystemHintCap = 10m;
    private const decimal AdminHintCap = 10m;

    private readonly IUnitysearchUsageStatStore _usageStats;
    private readonly IUnitysearchPairStatStore _pairStats;
    private readonly IUnitysearchRankingHintStore _hints;
    private readonly IUnitysearchDecisionTraceStore? _traceStore;
    private readonly UnityAiFeatureFlagAccessor _flags;
    private readonly ILogger<UnitysearchRankingEngine> _logger;
    private readonly Random _sampler;

    public UnitysearchRankingEngine(
        IUnitysearchUsageStatStore usageStats,
        IUnitysearchPairStatStore pairStats,
        IUnitysearchRankingHintStore hints,
        UnityAiFeatureFlagAccessor flags,
        ILogger<UnitysearchRankingEngine> logger,
        IUnitysearchDecisionTraceStore? traceStore = null)
    {
        _usageStats = usageStats;
        _pairStats = pairStats;
        _hints = hints;
        _traceStore = traceStore;
        _flags = flags;
        _logger = logger;
        _sampler = new Random();
    }

    public async Task<UnitysearchRankingResult> RankAsync(
        UnitysearchRankingRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Candidates.Count == 0)
        {
            return new UnitysearchRankingResult(Array.Empty<UnitysearchRankedCandidate>(), null);
        }

        var entityIds = request.Candidates.Select(c => c.EntityId).Distinct().ToArray();

        // 1. Frequency stats — separate user-level and company-level.
        IReadOnlyDictionary<Guid, UnitysearchUsageStatRecord> userStats =
            request.UserId is null
                ? new Dictionary<Guid, UnitysearchUsageStatRecord>()
                : await _usageStats.GetForCandidatesAsync(
                    request.CompanyId, request.UserId, UnitysearchScopeType.User,
                    request.Context, request.EntityType, entityIds, cancellationToken).ConfigureAwait(false);

        var companyStats = await _usageStats.GetForCandidatesAsync(
            request.CompanyId, null, UnitysearchScopeType.Company,
            request.Context, request.EntityType, entityIds, cancellationToken).ConfigureAwait(false);

        // 2. Pair stats — only when an anchor is supplied.
        IReadOnlyList<UnitysearchPairStatRecord> userPairs = Array.Empty<UnitysearchPairStatRecord>();
        IReadOnlyList<UnitysearchPairStatRecord> companyPairs = Array.Empty<UnitysearchPairStatRecord>();
        if (request.Anchor is not null)
        {
            if (request.UserId is not null)
            {
                userPairs = await _pairStats.GetForAnchorAsync(
                    request.CompanyId, request.UserId, UnitysearchScopeType.User,
                    request.Anchor.SourceContext, request.Anchor.AnchorEntityType, request.Anchor.AnchorEntityId,
                    request.Context, request.EntityType, cancellationToken).ConfigureAwait(false);
            }

            companyPairs = await _pairStats.GetForAnchorAsync(
                request.CompanyId, null, UnitysearchScopeType.Company,
                request.Anchor.SourceContext, request.Anchor.AnchorEntityType, request.Anchor.AnchorEntityId,
                request.Context, request.EntityType, cancellationToken).ConfigureAwait(false);
        }

        // 3. Active ranking hints (system / admin / ai). Pending and expired
        //    hints are filtered by the store; we double-check here too.
        var activeHints = (await _hints.GetActiveAsync(
                request.CompanyId, request.UserId,
                request.Context, request.EntityType, entityIds, cancellationToken).ConfigureAwait(false))
            .Where(h =>
                string.Equals(h.Status, UnitysearchHintStatus.Active, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(h.ValidationStatus, UnitysearchHintValidationStatus.Valid, StringComparison.OrdinalIgnoreCase) &&
                (h.ExpiresAt is null || h.ExpiresAt > DateTimeOffset.UtcNow))
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var query = (request.NormalizedQuery ?? request.Query)?.Trim().ToLowerInvariant();

        var scored = new List<UnitysearchScoreBreakdown>(request.Candidates.Count);
        foreach (var candidate in request.Candidates)
        {
            scored.Add(Score(
                candidate,
                query,
                userStats.GetValueOrDefault(candidate.EntityId),
                companyStats.GetValueOrDefault(candidate.EntityId),
                userPairs.Concat(companyPairs).Where(p => p.TargetEntityId == candidate.EntityId).ToList(),
                activeHints.Where(h => h.EntityId == candidate.EntityId).ToList(),
                now));
        }

        var ordered = scored
            .Select((s, i) => (Score: s, OriginalIndex: i))
            .OrderByDescending(x => x.Score.FinalScore)
            .ThenBy(x => x.OriginalIndex)
            .ToList();

        var ranked = new List<UnitysearchRankedCandidate>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (score, originalIndex) = ordered[i];
            ranked.Add(new UnitysearchRankedCandidate(
                Candidate: request.Candidates[originalIndex],
                Score: score,
                RankPosition: i));
        }

        Guid? traceId = null;
        if (request.TraceEnabled && _flags.UnitysearchTraceEnabled && _traceStore is not null)
        {
            // Gate: per-call sampling so production traces stay bounded.
            var sampleRate = Math.Clamp(_flags.UnitysearchTraceSampleRate, 0.0, 1.0);
            if (sampleRate >= 1.0 || (sampleRate > 0.0 && _sampler.NextDouble() < sampleRate))
            {
                traceId = await SafeWriteTraceAsync(request, ranked, cancellationToken).ConfigureAwait(false);
            }
        }

        return new UnitysearchRankingResult(ranked, traceId);
    }

    private static UnitysearchScoreBreakdown Score(
        UnitysearchRankingCandidate candidate,
        string? query,
        UnitysearchUsageStatRecord? userStat,
        UnitysearchUsageStatRecord? companyStat,
        IReadOnlyList<UnitysearchPairStatRecord> pairsForCandidate,
        IReadOnlyList<UnitysearchRankingHintRecord> hintsForCandidate,
        DateTimeOffset now)
    {
        var textMatch = ComputeTextMatch(candidate, query);

        var userFreq = ComputeFrequency(userStat?.SelectCount ?? 0, perUnit: 8m, cap: UserFrequencyCap);
        var companyFreq = ComputeFrequency(companyStat?.SelectCount ?? 0, perUnit: 4m, cap: CompanyFrequencyCap);

        var recency = ComputeRecency(userStat?.LastSelectedAt ?? companyStat?.LastSelectedAt, now);

        // Pair score uses the strongest confidence among (user, company) pair
        // rows for this target.
        decimal pair = 0m;
        if (pairsForCandidate.Count > 0)
        {
            var bestConfidence = pairsForCandidate.Max(p => p.ConfidenceScore);
            pair = Math.Min(bestConfidence * 20m, PairCap);
        }

        var alias = textMatch.aliasMatch ? 0m : 0m; // alias adjustment baked into textMatch.score

        decimal aiHint = 0m;
        foreach (var hint in hintsForCandidate)
        {
            var cap = hint.Source switch
            {
                UnitysearchHintSource.Ai => AiHintCap,
                UnitysearchHintSource.Admin => AdminHintCap,
                _ => SystemHintCap,
            };
            aiHint += Math.Min(hint.BoostScore, cap);
        }

        decimal status = candidate.IsActive ? 0m : -50m;

        decimal penalty = candidate.IsActive ? 0m : 0m;

        var final = textMatch.score + userFreq + companyFreq + recency + pair + alias + aiHint + status - penalty;

        return new UnitysearchScoreBreakdown(
            EntityId: candidate.EntityId,
            FinalScore: final,
            TextMatchScore: textMatch.score,
            UserFrequencyScore: userFreq,
            CompanyFrequencyScore: companyFreq,
            RecencyScore: recency,
            PairScore: pair,
            AliasScore: alias,
            AiHintScore: aiHint,
            StatusScore: status,
            PenaltyScore: penalty,
            Reason: ChooseReason(textMatch.score, userFreq, companyFreq, recency, pair, aiHint));
    }

    private static (decimal score, bool aliasMatch) ComputeTextMatch(UnitysearchRankingCandidate candidate, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (0m, false);
        }

        var code = candidate.DisplayCode?.ToLowerInvariant() ?? string.Empty;
        var name = candidate.DisplayName?.ToLowerInvariant() ?? string.Empty;

        if (string.Equals(code, query, StringComparison.Ordinal) || string.Equals(name, query, StringComparison.Ordinal))
        {
            return (100m, false);
        }

        if (code.StartsWith(query, StringComparison.Ordinal) || name.StartsWith(query, StringComparison.Ordinal))
        {
            return (80m, false);
        }

        if (code.Contains(query, StringComparison.Ordinal) || name.Contains(query, StringComparison.Ordinal))
        {
            return (50m, false);
        }

        if (candidate.AliasTerms is not null)
        {
            foreach (var alias in candidate.AliasTerms)
            {
                var a = alias?.ToLowerInvariant();
                if (string.IsNullOrEmpty(a))
                {
                    continue;
                }

                if (string.Equals(a, query, StringComparison.Ordinal) || a.StartsWith(query, StringComparison.Ordinal))
                {
                    return (40m, true);
                }
            }
        }

        return (0m, false);
    }

    private static decimal ComputeFrequency(int selectCount, decimal perUnit, decimal cap)
    {
        if (selectCount <= 0)
        {
            return 0m;
        }

        var raw = (decimal)Math.Log(selectCount + 1) * perUnit;
        return Math.Min(raw, cap);
    }

    private static decimal ComputeRecency(DateTimeOffset? lastSelectedAt, DateTimeOffset now)
    {
        if (lastSelectedAt is null)
        {
            return 0m;
        }

        var hours = (now - lastSelectedAt.Value).TotalHours;
        if (hours <= 24) return 15m;
        if (hours <= 24 * 7) return 10m;
        if (hours <= 24 * 30) return 5m;
        return 0m;
    }

    private static string? ChooseReason(decimal text, decimal userFreq, decimal companyFreq, decimal recency, decimal pair, decimal aiHint)
    {
        if (text >= 100m) return "Exact match";
        if (pair > 0m) return "Often used with selected anchor";
        if (recency >= 15m) return "Recently used";
        if (userFreq > 0m) return "Frequently used by you";
        if (companyFreq > 0m) return "Frequently used in this company";
        if (aiHint > 0m) return "Suggested from learned pattern";
        if (text >= 80m) return "Prefix match";
        if (text >= 50m) return "Substring match";
        if (text >= 40m) return "Alias match";
        return null;
    }

    private async Task<Guid?> SafeWriteTraceAsync(
        UnitysearchRankingRequest request,
        IReadOnlyList<UnitysearchRankedCandidate> ranked,
        CancellationToken cancellationToken)
    {
        try
        {
            var trace = ranked.Select(r => new
            {
                entity_id = r.Candidate.EntityId,
                final_score = r.Score.FinalScore,
                text_match_score = r.Score.TextMatchScore,
                user_frequency_score = r.Score.UserFrequencyScore,
                company_frequency_score = r.Score.CompanyFrequencyScore,
                recency_score = r.Score.RecencyScore,
                pair_score = r.Score.PairScore,
                alias_score = r.Score.AliasScore,
                ai_hint_score = r.Score.AiHintScore,
                status_score = r.Score.StatusScore,
                penalty_score = r.Score.PenaltyScore,
                rank_position = r.RankPosition,
                reason = r.Score.Reason,
            }).ToArray();

            var json = JsonSerializer.Serialize(new { results = trace });

            var traceId = await _traceStore!.WriteAsync(
                request.CompanyId, request.UserId,
                request.Context, request.EntityType,
                request.Query, request.NormalizedQuery,
                ranked.Count, json, cancellationToken).ConfigureAwait(false);

            return traceId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "unityAI decision trace write failed (context={Context})", request.Context);
            return null;
        }
    }
}
