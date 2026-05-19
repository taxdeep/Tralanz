using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Domain.Shared.Pricing;

namespace Citus.Modules.Inventory.Application.Pricing;

public sealed class ItemPriceResolver(IInventoryItemPriceStore store) : IItemPriceResolver
{
    public Task<InventoryItemPriceResolution?> ResolveAsync(
        InventoryItemPriceQuery query,
        CancellationToken cancellationToken)
    {
        if (query.CompanyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to resolve item pricing.");
        }

        if (query.ItemId == Guid.Empty)
        {
            throw new InvalidOperationException("Item id is required to resolve pricing.");
        }

        var currency = NormalizeCurrency(query.CurrencyCode);
        if (currency.Length != 3)
        {
            throw new InvalidOperationException($"Currency code must be a 3-letter ISO code; got '{query.CurrencyCode}'.");
        }

        // Normalize inputs before they hit SQL. Quantity floors at 1 so
        // a missing or zero value still matches the default tier; price
        // list code is uppercased + trimmed to a stable form.
        var normalizedQuery = query with
        {
            CurrencyCode = currency,
            PriceListCode = NormalizePriceListCode(query.PriceListCode),
            Quantity = query.Quantity > 0m ? query.Quantity : 1m,
        };

        return store.ResolveAsync(normalizedQuery, cancellationToken);
    }

    private static string NormalizeCurrency(string code) =>
        string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().ToUpperInvariant();

    private static string? NormalizePriceListCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
}
