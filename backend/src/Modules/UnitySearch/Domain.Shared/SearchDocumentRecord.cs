namespace Citus.Modules.UnitySearch.Domain.Shared;

/// <summary>
/// One row of the projected search index.
///
/// Batch-2 additions (<c>ModuleKey</c>, <c>RequiredPermissions</c>,
/// <c>OwnerUserId</c>, <c>VisibilityScope</c>) carry the per-row
/// isolation metadata the query SQL needs to enforce:
/// <list type="bullet">
///   <item>module on/off at company scope (<c>ModuleKey</c>),</item>
///   <item>permission gate (<c>RequiredPermissions</c>; empty = anyone in the company),</item>
///   <item>per-user assignee gate (<c>VisibilityScope='assignee_only'</c> + <c>OwnerUserId</c>).</item>
/// </list>
/// All four default to "no restriction" so existing call sites keep
/// compiling; seeders explicitly populate the right values per
/// entity type.
/// </summary>
public sealed record class SearchDocumentRecord(
    CompanyId CompanyId,
    string EntityType,
    Guid SourceId,
    string GroupKey,
    string PrimaryText,
    string SecondaryText,
    string SearchText,
    string ExactCodeNorm,
    string NavigationHref,
    string MetadataJson,
    DateOnly? EffectiveDate,
    decimal? Amount,
    bool IsActive,
    bool IsVoided,
    decimal RankBoost,
    long Version,
    decimal ComputedScore = 0m,
    string ModuleKey = "core",
    IReadOnlyList<string>? RequiredPermissions = null,
    UserId? OwnerUserId = null,
    string VisibilityScope = "company",
    string? VisibilityOverridePermission = null);
