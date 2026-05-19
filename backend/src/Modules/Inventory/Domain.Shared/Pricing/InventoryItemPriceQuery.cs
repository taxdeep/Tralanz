namespace Citus.Modules.Inventory.Domain.Shared.Pricing;

/// <summary>
/// Inputs to <c>IItemPriceResolver.ResolveAsync</c>. Optional fields
/// progressively narrow the match: <see cref="CustomerId"/> tries a
/// customer-specific override first; <see cref="PriceListCode"/> tries
/// the named book first; <see cref="Quantity"/> picks the highest
/// quantity tier whose <c>MinQuantity</c> is ≤ the request.
/// <see cref="AsOf"/> bounds the effective-date window (use the
/// document date — invoice date, task date, etc.).
/// </summary>
public sealed record class InventoryItemPriceQuery
{
    public required CompanyId CompanyId { get; init; }

    public required Guid ItemId { get; init; }

    public required string CurrencyCode { get; init; }

    public required DateOnly AsOf { get; init; }

    public Guid? CustomerId { get; init; }

    public string? PriceListCode { get; init; }

    public decimal Quantity { get; init; } = 1m;
}
