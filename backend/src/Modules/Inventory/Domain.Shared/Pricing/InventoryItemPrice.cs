namespace Citus.Modules.Inventory.Domain.Shared.Pricing;

/// <summary>
/// One row of the per-item price book. Multi-dimensional:
/// <list type="bullet">
///   <item><b>Currency</b> — every row is keyed by an ISO currency.</item>
///   <item><b>Quantity tier</b> — <see cref="MinQuantity"/> selects which
///     row wins for a purchase of N units (highest min_quantity ≤ N).</item>
///   <item><b>Customer scope</b> — non-null <see cref="CustomerId"/> is a
///     customer-specific override; null is the generic price.</item>
///   <item><b>Price list</b> — non-null <see cref="PriceListCode"/> ties
///     the row to a named book (e.g. "WHOLESALE"); null is the general
///     book.</item>
///   <item><b>Effective window</b> — <see cref="EffectiveFrom"/> and
///     <see cref="EffectiveTo"/> (nullable) bracket validity.</item>
/// </list>
/// Resolution priority (per <c>IItemPriceResolver</c>):
/// customer-specific &gt; price-list-specific &gt; highest quantity tier &gt;
/// most recent effective_from.
/// </summary>
public sealed record class InventoryItemPrice(
    Guid Id,
    CompanyId CompanyId,
    Guid ItemId,
    string CurrencyCode,
    decimal UnitPrice,
    decimal MinQuantity,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string? PriceListCode,
    Guid? CustomerId,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
