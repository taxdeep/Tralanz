using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Citus.Modules.UnityAi.Tests;

public sealed class UnitysearchRankingEngineTests
{
    private static readonly Guid CompanyA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CompanyB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserA = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task ExactMatch_RanksFirst()
    {
        var (engine, _, _, _) = BuildEngine();
        var request = new UnitysearchRankingRequest(
            CompanyId: CompanyA, UserId: UserA,
            Context: "expense.vendor_picker", EntityType: "vendor",
            Query: "amazon", NormalizedQuery: "amazon",
            Anchor: null,
            Candidates: new[]
            {
                new UnitysearchRankingCandidate(EntityId: Guid.NewGuid(), EntityType: "vendor", DisplayCode: "STAPLES", DisplayName: "Staples Inc"),
                new UnitysearchRankingCandidate(EntityId: Guid.NewGuid(), EntityType: "vendor", DisplayCode: "AMAZON",  DisplayName: "Amazon"),
            },
            TraceEnabled: false);

        var result = await engine.RankAsync(request, CancellationToken.None);

        Assert.Equal("AMAZON", result.Ranked[0].Candidate.DisplayCode);
        Assert.True(result.Ranked[0].Score.FinalScore > result.Ranked[1].Score.FinalScore);
        Assert.Equal("Exact match", result.Ranked[0].Score.Reason);
    }

    [Fact]
    public async Task FrequentSelection_BoostsCandidate()
    {
        var (engine, usage, _, _) = BuildEngine();
        var amazon = Guid.NewGuid();
        var staples = Guid.NewGuid();
        usage.Seed(CompanyA, UserA, UnitysearchScopeType.User, "expense.vendor_picker", "vendor", staples, selectCount: 30);

        var request = new UnitysearchRankingRequest(
            CompanyId: CompanyA, UserId: UserA,
            Context: "expense.vendor_picker", EntityType: "vendor",
            Query: null, NormalizedQuery: null, Anchor: null,
            Candidates: new[]
            {
                new UnitysearchRankingCandidate(EntityId: amazon,  EntityType: "vendor", DisplayCode: "AMAZON",  DisplayName: "Amazon"),
                new UnitysearchRankingCandidate(EntityId: staples, EntityType: "vendor", DisplayCode: "STAPLES", DisplayName: "Staples"),
            },
            TraceEnabled: false);

        var result = await engine.RankAsync(request, CancellationToken.None);

        Assert.Equal(staples, result.Ranked[0].Candidate.EntityId);
        Assert.True(result.Ranked[0].Score.UserFrequencyScore > 0);
    }

    [Fact]
    public async Task RecentSelection_BoostsAboveOlderOnes()
    {
        var (engine, usage, _, _) = BuildEngine();
        var fresh = Guid.NewGuid();
        var stale = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        usage.Seed(CompanyA, UserA, UnitysearchScopeType.User, "ctx", "vendor", fresh, selectCount: 1, lastSelectedAt: now.AddHours(-1));
        usage.Seed(CompanyA, UserA, UnitysearchScopeType.User, "ctx", "vendor", stale, selectCount: 1, lastSelectedAt: now.AddDays(-90));

        var request = new UnitysearchRankingRequest(
            CompanyA, UserA, "ctx", "vendor", null, null, null,
            new[]
            {
                new UnitysearchRankingCandidate(stale, "vendor", "B", "B"),
                new UnitysearchRankingCandidate(fresh, "vendor", "A", "A"),
            },
            TraceEnabled: false);

        var result = await engine.RankAsync(request, CancellationToken.None);
        Assert.Equal(fresh, result.Ranked[0].Candidate.EntityId);
    }

    [Fact]
    public async Task UserStats_OutweighCompanyStats()
    {
        var (engine, usage, _, _) = BuildEngine();
        var preferredByUser = Guid.NewGuid();
        var preferredByCompany = Guid.NewGuid();
        usage.Seed(CompanyA, UserA, UnitysearchScopeType.User, "ctx", "vendor", preferredByUser, selectCount: 5);
        usage.Seed(CompanyA, null, UnitysearchScopeType.Company, "ctx", "vendor", preferredByCompany, selectCount: 5);

        var request = new UnitysearchRankingRequest(
            CompanyA, UserA, "ctx", "vendor", null, null, null,
            new[]
            {
                new UnitysearchRankingCandidate(preferredByCompany, "vendor", "C", "C"),
                new UnitysearchRankingCandidate(preferredByUser, "vendor", "U", "U"),
            },
            TraceEnabled: false);

        var result = await engine.RankAsync(request, CancellationToken.None);
        Assert.Equal(preferredByUser, result.Ranked[0].Candidate.EntityId);
    }

    [Fact]
    public async Task PairStat_BoostsTargetForAnchor()
    {
        var (engine, _, pair, _) = BuildEngine();
        var officeSupplies = Guid.NewGuid();
        var amazonId = Guid.NewGuid();
        pair.Seed(CompanyA, UserA, UnitysearchScopeType.User,
            sourceContext: "expense.vendor_picker", anchorEntityType: "vendor", anchorEntityId: amazonId,
            targetContext: "expense.category_picker", targetEntityType: "account", targetEntityId: officeSupplies,
            confidence: 0.8m, selectCount: 8);

        var request = new UnitysearchRankingRequest(
            CompanyA, UserA, "expense.category_picker", "account", null, null,
            new UnitysearchRankingAnchor("expense.vendor_picker", "vendor", amazonId),
            new[]
            {
                new UnitysearchRankingCandidate(Guid.NewGuid(), "account", "OTHER", "Other"),
                new UnitysearchRankingCandidate(officeSupplies,   "account", "OFFICE", "Office Supplies"),
            },
            TraceEnabled: false);

        var result = await engine.RankAsync(request, CancellationToken.None);
        Assert.Equal(officeSupplies, result.Ranked[0].Candidate.EntityId);
        Assert.True(result.Ranked[0].Score.PairScore > 0);
        Assert.Equal("Often used with selected anchor", result.Ranked[0].Score.Reason);
    }

    [Fact]
    public async Task PendingHint_DoesNotAffectRanking()
    {
        var (engine, _, _, hints) = BuildEngine();
        var boosted = Guid.NewGuid();
        var other = Guid.NewGuid();
        hints.Seed(new UnitysearchRankingHintRecord(
            Id: Guid.NewGuid(), CompanyId: CompanyA, UserId: null,
            Context: "ctx", EntityType: "vendor", EntityId: boosted,
            BoostScore: 50m, Confidence: 1m, Reason: "test",
            Source: UnitysearchHintSource.Ai,
            Status: UnitysearchHintStatus.Pending,         // not active
            ValidationStatus: UnitysearchHintValidationStatus.Valid,
            ExpiresAt: null));

        var request = new UnitysearchRankingRequest(
            CompanyA, UserA, "ctx", "vendor", null, null, null,
            new[]
            {
                new UnitysearchRankingCandidate(boosted, "vendor", "A", "A"),
                new UnitysearchRankingCandidate(other,   "vendor", "B", "B"),
            },
            TraceEnabled: false);

        var result = await engine.RankAsync(request, CancellationToken.None);
        // No score for the boosted candidate; with no other signal, original
        // order should be preserved (deterministic tiebreak by original index).
        Assert.Equal(boosted, result.Ranked[0].Candidate.EntityId);
        Assert.Equal(0m, result.Ranked[0].Score.AiHintScore);
    }

    [Fact]
    public async Task ActiveAiHint_AddsBoundedBoost()
    {
        var (engine, _, _, hints) = BuildEngine();
        var boosted = Guid.NewGuid();
        var other = Guid.NewGuid();
        hints.Seed(new UnitysearchRankingHintRecord(
            Id: Guid.NewGuid(), CompanyId: CompanyA, UserId: null,
            Context: "ctx", EntityType: "vendor", EntityId: boosted,
            BoostScore: 100m,                              // gateway tries to push 100
            Confidence: 1m, Reason: "test",
            Source: UnitysearchHintSource.Ai,
            Status: UnitysearchHintStatus.Active,
            ValidationStatus: UnitysearchHintValidationStatus.Valid,
            ExpiresAt: null));

        var request = new UnitysearchRankingRequest(
            CompanyA, UserA, "ctx", "vendor", null, null, null,
            new[]
            {
                new UnitysearchRankingCandidate(other,   "vendor", "X", "X"),
                new UnitysearchRankingCandidate(boosted, "vendor", "Y", "Y"),
            },
            TraceEnabled: false);

        var result = await engine.RankAsync(request, CancellationToken.None);
        Assert.Equal(boosted, result.Ranked[0].Candidate.EntityId);
        // AI boost is capped at 5 — even though store says 100.
        Assert.Equal(5m, result.Ranked[0].Score.AiHintScore);
    }

    [Fact]
    public async Task CrossCompanyHint_IsNotApplied()
    {
        var (engine, _, _, hints) = BuildEngine();
        var sharedEntity = Guid.NewGuid();
        hints.Seed(new UnitysearchRankingHintRecord(
            Id: Guid.NewGuid(), CompanyId: CompanyB, UserId: null,   // company B
            Context: "ctx", EntityType: "vendor", EntityId: sharedEntity,
            BoostScore: 100m, Confidence: 1m, Reason: null,
            Source: UnitysearchHintSource.Admin,
            Status: UnitysearchHintStatus.Active,
            ValidationStatus: UnitysearchHintValidationStatus.Valid,
            ExpiresAt: null));

        var request = new UnitysearchRankingRequest(
            CompanyA, UserA, "ctx", "vendor", null, null, null,         // ranking for company A
            new[]
            {
                new UnitysearchRankingCandidate(sharedEntity, "vendor", "X", "X"),
            },
            TraceEnabled: false);

        var result = await engine.RankAsync(request, CancellationToken.None);
        Assert.Equal(0m, result.Ranked[0].Score.AiHintScore);
    }

    [Fact]
    public async Task ExpiredHint_IsIgnored()
    {
        var (engine, _, _, hints) = BuildEngine();
        var entity = Guid.NewGuid();
        hints.Seed(new UnitysearchRankingHintRecord(
            Id: Guid.NewGuid(), CompanyId: CompanyA, UserId: null,
            Context: "ctx", EntityType: "vendor", EntityId: entity,
            BoostScore: 100m, Confidence: 1m, Reason: null,
            Source: UnitysearchHintSource.System,
            Status: UnitysearchHintStatus.Active,
            ValidationStatus: UnitysearchHintValidationStatus.Valid,
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(-1)));   // expired

        var request = new UnitysearchRankingRequest(
            CompanyA, UserA, "ctx", "vendor", null, null, null,
            new[] { new UnitysearchRankingCandidate(entity, "vendor", "X", "X") },
            TraceEnabled: false);

        var result = await engine.RankAsync(request, CancellationToken.None);
        Assert.Equal(0m, result.Ranked[0].Score.AiHintScore);
    }

    [Fact]
    public async Task TraceDisabledByDefault_ReturnsNoTraceId()
    {
        var (engine, _, _, _) = BuildEngine();
        var request = new UnitysearchRankingRequest(
            CompanyA, UserA, "ctx", "vendor", "anything", "anything", null,
            new[] { new UnitysearchRankingCandidate(Guid.NewGuid(), "vendor", "X", "X") },
            TraceEnabled: true); // request asks, but flag default off

        var result = await engine.RankAsync(request, CancellationToken.None);
        Assert.Null(result.TraceId);
    }

    [Fact]
    public async Task CompanyIsolation_OneCompanysStatsDoNotBleedToAnother()
    {
        var (engine, usage, _, _) = BuildEngine();
        var entity = Guid.NewGuid();
        usage.Seed(CompanyA, UserA, UnitysearchScopeType.User, "ctx", "vendor", entity, selectCount: 100);

        var request = new UnitysearchRankingRequest(
            CompanyB, UserA, "ctx", "vendor", null, null, null,            // ranking for company B
            new[] { new UnitysearchRankingCandidate(entity, "vendor", "X", "X") },
            TraceEnabled: false);

        var result = await engine.RankAsync(request, CancellationToken.None);
        Assert.Equal(0m, result.Ranked[0].Score.UserFrequencyScore);
    }

    private static (UnitysearchRankingEngine Engine, InMemoryUsageStatStore Usage, InMemoryPairStatStore Pair, InMemoryRankingHintStore Hints) BuildEngine()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var flags = new UnityAiFeatureFlagAccessor(config);
        var usage = new InMemoryUsageStatStore();
        var pair = new InMemoryPairStatStore();
        var hints = new InMemoryRankingHintStore();
        var engine = new UnitysearchRankingEngine(usage, pair, hints, flags, NullLogger<UnitysearchRankingEngine>.Instance);
        return (engine, usage, pair, hints);
    }
}
