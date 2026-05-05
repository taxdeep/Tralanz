using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Citus.Modules.UnitySearch.Application.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Citus.Modules.UnityAi.Tests;

public sealed class UnitysearchAiRerankingEngineTests
{
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task FrequentSelection_RerankerMovesItemUp()
    {
        var amazon = Guid.NewGuid();
        var staples = Guid.NewGuid();
        var inner = new FakeInnerEngine(BuildResult(staples, amazon));
        var (engine, usage, _, _) = BuildDecorator(inner, learningEnabled: true);
        usage.Seed(CompanyA, UserA, UnitysearchScopeType.User, "expense.vendor_picker", "vendor", amazon, selectCount: 30);

        var result = await engine.SearchAsync(BuildQuery(""), CancellationToken.None);

        var items = result.Groups.Single().Items;
        Assert.Equal(amazon, items[0].SourceId);
        Assert.Equal(staples, items[1].SourceId);
    }

    [Fact]
    public async Task LearningFlagOff_FallsThroughToInnerOrder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var inner = new FakeInnerEngine(BuildResult(first, second));
        var (engine, usage, _, _) = BuildDecorator(inner, learningEnabled: false);
        // Seed strong evidence for `second` — but learning is off, so it
        // should not affect order.
        usage.Seed(CompanyA, UserA, UnitysearchScopeType.User, "expense.vendor_picker", "vendor", second, selectCount: 100);

        var result = await engine.SearchAsync(BuildQuery(""), CancellationToken.None);

        var items = result.Groups.Single().Items;
        Assert.Equal(first, items[0].SourceId);
        Assert.Equal(second, items[1].SourceId);
    }

    [Fact]
    public async Task RankingEngineThrowing_ReturnsInnerResultUnchanged()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var inner = new FakeInnerEngine(BuildResult(first, second));
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var flags = new UnityAiFeatureFlagAccessor(config);
        var ranking = new ThrowingRankingEngine();
        var decorator = new UnitysearchAiRerankingEngine(inner, ranking, flags, NullLogger<UnitysearchAiRerankingEngine>.Instance);

        var result = await decorator.SearchAsync(BuildQuery(""), CancellationToken.None);

        var items = result.Groups.Single().Items;
        Assert.Equal(first, items[0].SourceId);
        Assert.Equal(second, items[1].SourceId);
    }

    [Fact]
    public async Task SingleItemGroup_PassedThroughUnchanged()
    {
        var only = Guid.NewGuid();
        var inner = new FakeInnerEngine(BuildResult(only));
        var (engine, _, _, _) = BuildDecorator(inner, learningEnabled: true);

        var result = await engine.SearchAsync(BuildQuery(""), CancellationToken.None);

        Assert.Single(result.Groups.Single().Items);
        Assert.Equal(only, result.Groups.Single().Items[0].SourceId);
    }

    [Fact]
    public async Task PassThroughMethods_DelegateToInner()
    {
        var inner = new FakeInnerEngine(BuildResult(Guid.NewGuid()));
        var (engine, _, _, _) = BuildDecorator(inner, learningEnabled: true);

        await engine.RecordClickAsync(CompanyA, UserA, "ctx", "vendor", Guid.NewGuid(), CancellationToken.None);
        await engine.ListRecentQueriesAsync(CompanyA, UserA, "ctx", 5, CancellationToken.None);
        await engine.ListRecentSelectionsAsync(CompanyA, UserA, "ctx", 5, CancellationToken.None);

        Assert.Equal(1, inner.RecordClickCalls);
        Assert.Equal(1, inner.RecentQueriesCalls);
        Assert.Equal(1, inner.RecentSelectionsCalls);
    }

    private static UnitySearchQuery BuildQuery(string searchText) => new()
    {
        CompanyId = CompanyA,
        UserId = UserA,
        Context = "expense.vendor_picker",
        SearchText = searchText,
        Take = 10,
    };

    private static UnitySearchResult BuildResult(params Guid[] entityIds) => new()
    {
        QueryText = string.Empty,
        Context = "expense.vendor_picker",
        Groups = new[]
        {
            new UnitySearchGroupResult
            {
                GroupKey = "vendor",
                Title = "Vendors",
                Items = entityIds.Select(id => new UnitySearchSuggestion
                {
                    SourceId = id,
                    EntityType = "vendor",
                    GroupKey = "vendor",
                    PrimaryText = id.ToString("N")[..6],
                    SecondaryText = id.ToString("N")[..6],
                    NavigationHref = "/",
                    Score = 0m,
                }).ToList(),
            }
        },
        TotalCount = entityIds.Length,
    };

    private static (UnitysearchAiRerankingEngine Engine, InMemoryUsageStatStore Usage, InMemoryPairStatStore Pair, InMemoryRankingHintStore Hints)
        BuildDecorator(IUnitySearchEngine inner, bool learningEnabled)
    {
        var settings = new Dictionary<string, string?>
        {
            ["UNITYSEARCH_LEARNING_ENABLED"] = learningEnabled ? "true" : "false",
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var flags = new UnityAiFeatureFlagAccessor(config);
        var usage = new InMemoryUsageStatStore();
        var pair = new InMemoryPairStatStore();
        var hints = new InMemoryRankingHintStore();
        var ranking = new UnitysearchRankingEngine(usage, pair, hints, flags, NullLogger<UnitysearchRankingEngine>.Instance);
        var decorator = new UnitysearchAiRerankingEngine(inner, ranking, flags, NullLogger<UnitysearchAiRerankingEngine>.Instance);
        return (decorator, usage, pair, hints);
    }

    private sealed class FakeInnerEngine : IUnitySearchEngine
    {
        private readonly UnitySearchResult _result;
        public int RecordClickCalls { get; private set; }
        public int RecentQueriesCalls { get; private set; }
        public int RecentSelectionsCalls { get; private set; }

        public FakeInnerEngine(UnitySearchResult result) => _result = result;

        public Task<UnitySearchResult> SearchAsync(UnitySearchQuery query, CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<IReadOnlyList<UnitySearchRecentQueryRecord>> ListRecentQueriesAsync(CompanyId companyId, UserId userId, string context, int take, CancellationToken cancellationToken)
        {
            RecentQueriesCalls++;
            return Task.FromResult<IReadOnlyList<UnitySearchRecentQueryRecord>>(Array.Empty<UnitySearchRecentQueryRecord>());
        }

        public Task<IReadOnlyList<UnitySearchRecentSelectionRecord>> ListRecentSelectionsAsync(CompanyId companyId, UserId userId, string context, int take, CancellationToken cancellationToken)
        {
            RecentSelectionsCalls++;
            return Task.FromResult<IReadOnlyList<UnitySearchRecentSelectionRecord>>(Array.Empty<UnitySearchRecentSelectionRecord>());
        }

        public Task RecordClickAsync(CompanyId companyId, UserId userId, string context, string entityType, Guid sourceId, CancellationToken cancellationToken)
        {
            RecordClickCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRankingEngine : IUnitysearchRankingEngine
    {
        public Task<UnitysearchRankingResult> RankAsync(UnitysearchRankingRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("synthetic failure");
    }
}
