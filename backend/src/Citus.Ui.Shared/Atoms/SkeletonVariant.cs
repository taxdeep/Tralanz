namespace Citus.Ui.Shared.Atoms;

/// <summary>
/// Layout shape that <see cref="CitusSkeleton"/> renders. Each value
/// matches one of the common page archetypes used across the Tralanz
/// Books shell.
/// </summary>
public enum SkeletonVariant
{
    /// <summary>List page — header bar plus N grid rows.</summary>
    List,
    /// <summary>Detail page — 4 stat cards + actions panel + 2-col content (lines + aside).</summary>
    Detail,
    /// <summary>Create / edit page — main form sections + aside (totals + actions).</summary>
    Form
}
