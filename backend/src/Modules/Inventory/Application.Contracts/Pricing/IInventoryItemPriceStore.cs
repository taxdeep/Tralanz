using Citus.Modules.Inventory.Domain.Shared.Pricing;

namespace Citus.Modules.Inventory.Application.Contracts.Pricing;

public interface IInventoryItemPriceStore
{
    /// <summary>Idempotent DDL bootstrap; safe to call repeatedly.</summary>
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<InventoryItemPrice>> ListAsync(
        CompanyId companyId,
        Guid itemId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<InventoryItemPrice?> GetAsync(
        CompanyId companyId,
        Guid priceId,
        CancellationToken cancellationToken);

    Task<InventoryItemPrice> UpsertAsync(
        CompanyId companyId,
        Guid itemId,
        InventoryItemPriceUpsertRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Soft delete: sets <c>is_active=false</c>. Audit-only callers
    /// can still read inactive rows via
    /// <see cref="ListAsync"/> with <c>includeInactive=true</c>.
    /// </summary>
    Task<bool> SoftDeleteAsync(
        CompanyId companyId,
        Guid priceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the best-matching price row for the query, or <c>null</c>
    /// when nothing matches (no row at all in the requested currency, or
    /// no row inside the effective window for the requested as-of date,
    /// or no row whose quantity tier covers <see cref="InventoryItemPriceQuery.Quantity"/>).
    /// Callers fall back to whatever default they prefer.
    /// </summary>
    Task<InventoryItemPriceResolution?> ResolveAsync(
        InventoryItemPriceQuery query,
        CancellationToken cancellationToken);
}
