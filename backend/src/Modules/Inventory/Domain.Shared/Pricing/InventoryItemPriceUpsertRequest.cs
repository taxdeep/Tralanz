namespace Citus.Modules.Inventory.Domain.Shared.Pricing;

/// <summary>
/// Input for create / update on a single price row.
/// <see cref="Id"/> is null for create, set for update. Currency is
/// uppercased, price-list codes are uppercased + trimmed by the store.
/// </summary>
public sealed record class InventoryItemPriceUpsertRequest
{
    public Guid? Id { get; init; }

    public required string CurrencyCode { get; init; }

    public required decimal UnitPrice { get; init; }

    public decimal MinQuantity { get; init; } = 1m;

    public required DateOnly EffectiveFrom { get; init; }

    public DateOnly? EffectiveTo { get; init; }

    public string? PriceListCode { get; init; }

    public Guid? CustomerId { get; init; }

    public bool IsActive { get; init; } = true;
}
