namespace Citus.Business.Blazor.Configuration;

/// <summary>
/// Per-deployment feature flags that gate operator-facing entry points.
/// Defaults are conservative — production deployments should explicitly
/// turn flags on once their dependencies are pilot-ready.
/// </summary>
/// <remarks>
/// Bound to the <c>FeatureFlags</c> configuration section, e.g. via
/// <c>FeatureFlags__InventoryActivationEntryEnabled=true</c> in the
/// systemd EnvironmentFile. Pages read these via
/// <c>IOptions&lt;FeatureFlagsOptions&gt;</c>.
/// </remarks>
public sealed class FeatureFlagsOptions
{
    public const string SectionName = "FeatureFlags";

    /// <summary>
    /// When false (the default), the "Enable Tralanz Inventory" CTAs on
    /// /items, /company/warehouses, etc. are hidden and the activation
    /// wizard at /company/inventory/activate is unreachable from the
    /// nav. The wizard route stays mounted so an operator who has
    /// bookmarked it can still access it directly — this flag is about
    /// preventing accidental activation, not blocking developers.
    ///
    /// Reason for the default: the full inventory write surface
    /// (Receipt / Shipment / Adjustment / Return / Manufacturing pages)
    /// has not landed yet. Activating today leaves the operator with
    /// the GR/IR clearing account that no UI can settle, and an "Items
    /// must have a Stock kind" UI gate that they can't satisfy.
    /// Re-enable once the V2 inventory write pages ship.
    /// </summary>
    public bool InventoryActivationEntryEnabled { get; set; }
}
