namespace Citus.Modules.Inventory.Domain.Shared.Pricing;

/// <summary>
/// Result of a successful price resolution. <see cref="MatchedScope"/>
/// records which priority tier the price came from so callers can
/// surface "this is a customer-specific override" badges in UIs.
/// </summary>
public sealed record class InventoryItemPriceResolution
{
    public required Guid PriceId { get; init; }

    public required CompanyId CompanyId { get; init; }

    public required Guid ItemId { get; init; }

    public required string CurrencyCode { get; init; }

    public required decimal UnitPrice { get; init; }

    public required decimal MinQuantity { get; init; }

    public required DateOnly EffectiveFrom { get; init; }

    public DateOnly? EffectiveTo { get; init; }

    public string? PriceListCode { get; init; }

    public Guid? CustomerId { get; init; }

    public required InventoryItemPriceScope MatchedScope { get; init; }
}

public enum InventoryItemPriceScope
{
    /// <summary>Generic, non-customer, non-price-list row.</summary>
    Generic = 0,

    /// <summary>Generic customer, but tied to a named price list (e.g. WHOLESALE).</summary>
    PriceList = 1,

    /// <summary>Customer-specific row in the general (null) book.</summary>
    Customer = 2,

    /// <summary>Customer-specific row inside a named price list.</summary>
    CustomerPriceList = 3,
}
