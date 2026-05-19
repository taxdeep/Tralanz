namespace Modules.Company.FeatureManagement;

/// <summary>
/// Catalog of per-company module flags that the platform recognizes.
///
/// Only keys listed here can be toggled. The catalog is intentionally
/// short — it grows when a later batch wires a new business module
/// (Task, etc.) to the company-feature gate. "Always-on" baseline
/// accounting modules (AR / AP / GL / Inventory) are not in the
/// catalog; they don't pass through this gate.
/// </summary>
public static class CompanyModuleFlagCatalog
{
    /// <summary>
    /// Task module: service-delivery execution unit that records
    /// completed work, accepts AP direct costs, and signals AR to bill.
    /// Wired in a later batch; the entry exists here so SysAdmin can
    /// flip the per-company switch before the Task module ships.
    /// </summary>
    public const string Task = "task";

    public static IReadOnlyList<CompanyModuleFlagOption> Options { get; } =
    [
        new(
            Task,
            "Task",
            "Service-delivery execution units. Records work done, accepts AP direct cost, and signals AR for billing."),
    ];

    public static IReadOnlyList<string> KnownKeys { get; } =
        Options.Select(static option => option.Key).ToArray();

    /// <summary>
    /// Trim + lowercase a candidate key and ensure it appears in the
    /// catalog. Throws when the key is unknown; callers are expected
    /// to surface a 404 / validation error from this exception.
    /// </summary>
    public static string NormalizeKey(string moduleKey)
    {
        if (string.IsNullOrWhiteSpace(moduleKey))
        {
            throw new InvalidOperationException("Module key is required.");
        }

        var normalized = moduleKey.Trim().ToLowerInvariant();
        if (!KnownKeys.Contains(normalized, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Unknown company module flag key '{normalized}'.");
        }

        return normalized;
    }

    public static bool IsKnown(string moduleKey)
    {
        if (string.IsNullOrWhiteSpace(moduleKey))
        {
            return false;
        }

        var normalized = moduleKey.Trim().ToLowerInvariant();
        return KnownKeys.Contains(normalized, StringComparer.Ordinal);
    }
}
