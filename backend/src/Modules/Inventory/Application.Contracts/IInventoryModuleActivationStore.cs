namespace Citus.Modules.Inventory.Application.Contracts;

/// <summary>
/// Owns the company-level switches that turn the Inventory paid module
/// on for a tenant. Pairs with <see cref="IInventoryFoundationStore"/>
/// (which owns warehouses + items + costing policy) by stamping the
/// per-company flags on the platform <c>companies</c> table.
///
/// Kept narrow on purpose: only the activation lifecycle. Item /
/// warehouse / policy mutation continues to flow through
/// <c>IInventoryFoundationStore</c>; cost-layer / receipt mutation
/// continues to flow through the per-document Phase D stores.
/// </summary>
public interface IInventoryModuleActivationStore
{
    /// <summary>
    /// Idempotent: if the company already has the flag set, refresh
    /// <c>profile_tag</c> only and leave <c>enabled_at</c> untouched.
    /// Returns the post-call state so the caller can show "already
    /// active" vs "newly activated" UX.
    /// </summary>
    Task<InventoryModuleActivationStateRecord> MarkEnabledAsync(
        CompanyId companyId,
        string profileTag,
        CancellationToken cancellationToken);

    Task<InventoryModuleActivationStateRecord?> GetStateAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);
}

public sealed record class InventoryModuleActivationStateRecord(
    CompanyId CompanyId,
    bool ModuleEnabled,
    DateTimeOffset? EnabledAt,
    DateTimeOffset? LockedAt,
    string? ProfileTag);
