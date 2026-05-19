using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Application.Pricing;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using SharedKernel.Identity;

namespace Citus.SysAdmin.Api.Tests;

/// <summary>
/// Resolver-layer behaviour: input normalization + the contract it
/// gives the store. The end-to-end SQL ordering (customer ▶ price
/// list ▶ tier ▶ recency) is exercised by integration tests against a
/// real Postgres; here we pin the invariants the resolver itself
/// owns, with a fake store capturing the normalized query.
/// </summary>
public class InventoryItemPriceResolverTests
{
    private static readonly CompanyId CompanyA = CompanyId.FromOrdinal(1);
    private static readonly Guid ItemId = Guid.NewGuid();
    private static readonly DateOnly AsOf = new(2026, 5, 18);

    [Fact]
    public async Task Resolver_uppercases_currency_and_price_list_before_querying_the_store()
    {
        var store = new RecordingPriceStore();
        var resolver = new ItemPriceResolver(store);

        await resolver.ResolveAsync(
            new InventoryItemPriceQuery
            {
                CompanyId = CompanyA,
                ItemId = ItemId,
                CurrencyCode = "  cad ",
                AsOf = AsOf,
                PriceListCode = "  wholesale  ",
            },
            CancellationToken.None);

        Assert.Equal("CAD", store.LastQuery!.CurrencyCode);
        Assert.Equal("WHOLESALE", store.LastQuery.PriceListCode);
    }

    [Fact]
    public async Task Resolver_collapses_blank_price_list_code_to_null()
    {
        var store = new RecordingPriceStore();
        var resolver = new ItemPriceResolver(store);

        await resolver.ResolveAsync(
            new InventoryItemPriceQuery
            {
                CompanyId = CompanyA,
                ItemId = ItemId,
                CurrencyCode = "USD",
                AsOf = AsOf,
                PriceListCode = "   ",
            },
            CancellationToken.None);

        Assert.Null(store.LastQuery!.PriceListCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Resolver_floors_non_positive_quantity_at_one(decimal input)
    {
        var store = new RecordingPriceStore();
        var resolver = new ItemPriceResolver(store);

        await resolver.ResolveAsync(
            new InventoryItemPriceQuery
            {
                CompanyId = CompanyA,
                ItemId = ItemId,
                CurrencyCode = "USD",
                AsOf = AsOf,
                Quantity = input,
            },
            CancellationToken.None);

        Assert.Equal(1m, store.LastQuery!.Quantity);
    }

    [Fact]
    public async Task Resolver_passes_through_explicit_quantity_unchanged()
    {
        var store = new RecordingPriceStore();
        var resolver = new ItemPriceResolver(store);

        await resolver.ResolveAsync(
            new InventoryItemPriceQuery
            {
                CompanyId = CompanyA,
                ItemId = ItemId,
                CurrencyCode = "USD",
                AsOf = AsOf,
                Quantity = 12.5m,
            },
            CancellationToken.None);

        Assert.Equal(12.5m, store.LastQuery!.Quantity);
    }

    [Fact]
    public async Task Resolver_rejects_non_three_letter_currency()
    {
        var resolver = new ItemPriceResolver(new RecordingPriceStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(
                new InventoryItemPriceQuery
                {
                    CompanyId = CompanyA,
                    ItemId = ItemId,
                    CurrencyCode = "DOLLARS",
                    AsOf = AsOf,
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task Resolver_rejects_empty_item_id()
    {
        var resolver = new ItemPriceResolver(new RecordingPriceStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(
                new InventoryItemPriceQuery
                {
                    CompanyId = CompanyA,
                    ItemId = Guid.Empty,
                    CurrencyCode = "USD",
                    AsOf = AsOf,
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task Resolver_returns_null_when_store_has_no_match()
    {
        var store = new RecordingPriceStore { NextResult = null };
        var resolver = new ItemPriceResolver(store);

        var result = await resolver.ResolveAsync(
            new InventoryItemPriceQuery
            {
                CompanyId = CompanyA,
                ItemId = ItemId,
                CurrencyCode = "USD",
                AsOf = AsOf,
            },
            CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class RecordingPriceStore : IInventoryItemPriceStore
    {
        public InventoryItemPriceQuery? LastQuery { get; private set; }

        public InventoryItemPriceResolution? NextResult { get; set; }

        public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<InventoryItemPrice>> ListAsync(CompanyId companyId, Guid itemId, bool includeInactive, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<InventoryItemPrice>>(Array.Empty<InventoryItemPrice>());

        public Task<InventoryItemPrice?> GetAsync(CompanyId companyId, Guid priceId, CancellationToken ct) =>
            Task.FromResult<InventoryItemPrice?>(null);

        public Task<InventoryItemPrice> UpsertAsync(CompanyId companyId, Guid itemId, InventoryItemPriceUpsertRequest request, CancellationToken ct) =>
            throw new NotImplementedException("Resolver tests don't exercise upsert.");

        public Task<bool> SoftDeleteAsync(CompanyId companyId, Guid priceId, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<InventoryItemPriceResolution?> ResolveAsync(InventoryItemPriceQuery query, CancellationToken ct)
        {
            LastQuery = query;
            return Task.FromResult(NextResult);
        }
    }
}
