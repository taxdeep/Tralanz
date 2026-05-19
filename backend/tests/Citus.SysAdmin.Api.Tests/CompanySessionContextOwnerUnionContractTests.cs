using Modules.CompanyAccess.Memberships;

namespace Citus.SysAdmin.Api.Tests;

/// <summary>
/// Batch 3.6 contract: the session-load Owner Union reads its source
/// of truth from <see cref="CompanyMembershipPermissionCatalog.AllTokens"/>.
/// These tests pin invariants the Union relies on so a future catalog
/// edit can't silently break the owner-fall-back path. The Postgres
/// store wiring itself is covered by integration tests against a real
/// database.
/// </summary>
public class CompanySessionContextOwnerUnionContractTests
{
    [Fact]
    public void Catalog_AllTokens_is_non_empty_so_union_actually_adds_tokens()
    {
        Assert.NotEmpty(CompanyMembershipPermissionCatalog.AllTokens);
    }

    [Fact]
    public void Catalog_AllTokens_contains_owner_critical_meta_permissions()
    {
        // settings.permissions.assign is the meta-permission that
        // gates "who can grant permissions". An owner must always
        // hold it, even if the persisted permissions array is stale.
        Assert.Contains("settings.permissions.assign", CompanyMembershipPermissionCatalog.AllTokens);
        Assert.Contains("settings.modules.toggle", CompanyMembershipPermissionCatalog.AllTokens);
        Assert.Contains("settings.company.edit", CompanyMembershipPermissionCatalog.AllTokens);
    }

    [Fact]
    public void Catalog_AllTokens_covers_every_module_for_owner_full_access()
    {
        var tokens = CompanyMembershipPermissionCatalog.AllTokens;
        Assert.Contains(tokens, t => t.StartsWith("ar.", StringComparison.Ordinal));
        Assert.Contains(tokens, t => t.StartsWith("ap.", StringComparison.Ordinal));
        Assert.Contains(tokens, t => t.StartsWith("gl.", StringComparison.Ordinal));
        Assert.Contains(tokens, t => t.StartsWith("inventory.", StringComparison.Ordinal));
        Assert.Contains(tokens, t => t.StartsWith("task.", StringComparison.Ordinal));
        Assert.Contains(tokens, t => t.StartsWith("reports.", StringComparison.Ordinal));
        Assert.Contains(tokens, t => t.StartsWith("settings.", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_AllTokens_has_no_blank_or_duplicate_entries()
    {
        var tokens = CompanyMembershipPermissionCatalog.AllTokens;
        Assert.All(tokens, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(tokens.Distinct(StringComparer.Ordinal).Count(), tokens.Count);
    }

    [Fact]
    public void Owner_preset_expansion_equals_AllTokens()
    {
        // The transfer-ownership pathway persists this same set into
        // the new owner's permissions column. The session-load Union
        // appends the same set on every request. The two must agree
        // so the persisted snapshot and the runtime view are
        // semantically identical for owners.
        var preset = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Owner);
        Assert.Equal(
            CompanyMembershipPermissionCatalog.AllTokens.OrderBy(t => t, StringComparer.Ordinal),
            preset.OrderBy(t => t, StringComparer.Ordinal));
    }
}
