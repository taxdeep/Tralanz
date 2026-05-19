using Citus.Modules.Inventory.Domain.Shared.Pricing;

namespace Citus.Modules.Inventory.Application.Contracts.Pricing;

/// <summary>
/// The single authoritative entry point for resolving a price.
/// Task / Quote / Invoice / Sales Order flows that want pricing must
/// call this — never read <c>inventory_item_prices</c> directly. The
/// resolver normalizes inputs (uppercase currency / price-list,
/// minimum quantity floor of 1) before delegating to the store.
/// </summary>
public interface IItemPriceResolver
{
    Task<InventoryItemPriceResolution?> ResolveAsync(
        InventoryItemPriceQuery query,
        CancellationToken cancellationToken);
}
