namespace Citus.Modules.UnityAi.Application.Contracts;

/// <summary>
/// Candidate fed to the ranking engine. Already context-validated and
/// permission-filtered by the caller.
/// </summary>
public sealed record UnitysearchRankingCandidate(
    Guid EntityId,
    string EntityType,
    string DisplayCode,
    string DisplayName,
    IReadOnlyList<string>? AliasTerms = null,
    bool IsActive = true,
    string? StatusLabel = null);

/// <summary>
/// Anchor entity for pair-stat boosts. e.g. when ranking expense.category
/// candidates after the user selected a vendor, the vendor goes here.
/// </summary>
public sealed record UnitysearchRankingAnchor(
    string SourceContext,
    string AnchorEntityType,
    Guid AnchorEntityId);

public sealed record UnitysearchRankingRequest(
    CompanyId CompanyId,
    Guid? UserId,
    string Context,
    string EntityType,
    string? Query,
    string? NormalizedQuery,
    UnitysearchRankingAnchor? Anchor,
    IReadOnlyList<UnitysearchRankingCandidate> Candidates,
    bool TraceEnabled);

public sealed record UnitysearchScoreBreakdown(
    Guid EntityId,
    decimal FinalScore,
    decimal TextMatchScore,
    decimal UserFrequencyScore,
    decimal CompanyFrequencyScore,
    decimal RecencyScore,
    decimal PairScore,
    decimal AliasScore,
    decimal AiHintScore,
    decimal StatusScore,
    decimal PenaltyScore,
    string? Reason);

public sealed record UnitysearchRankedCandidate(
    UnitysearchRankingCandidate Candidate,
    UnitysearchScoreBreakdown Score,
    int RankPosition);

public sealed record UnitysearchRankingResult(
    IReadOnlyList<UnitysearchRankedCandidate> Ranked,
    Guid? TraceId);

public interface IUnitysearchRankingEngine
{
    Task<UnitysearchRankingResult> RankAsync(
        UnitysearchRankingRequest request,
        CancellationToken cancellationToken);
}

public interface IUnitysearchDecisionTraceStore
{
    Task<Guid> WriteAsync(
        CompanyId companyId,
        UserId? userId,
        string context,
        string entityType,
        string? query,
        string? normalizedQuery,
        int? returnedCount,
        string traceJson,
        CancellationToken cancellationToken);
}
