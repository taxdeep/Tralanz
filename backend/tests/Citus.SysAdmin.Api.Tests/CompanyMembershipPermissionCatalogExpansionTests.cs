using Modules.CompanyAccess.Memberships;

namespace Citus.SysAdmin.Api.Tests;

public class CompanyMembershipPermissionCatalogExpansionTests
{
    [Fact]
    public void Catalog_keeps_all_eight_legacy_tokens()
    {
        Assert.Contains(CompanyMembershipPermissionCatalog.Ar, CompanyMembershipPermissionCatalog.LegacyTokens);
        Assert.Contains(CompanyMembershipPermissionCatalog.Ap, CompanyMembershipPermissionCatalog.LegacyTokens);
        Assert.Contains(CompanyMembershipPermissionCatalog.Approve, CompanyMembershipPermissionCatalog.LegacyTokens);
        Assert.Contains(CompanyMembershipPermissionCatalog.Reports, CompanyMembershipPermissionCatalog.LegacyTokens);
        Assert.Contains(CompanyMembershipPermissionCatalog.SettingsAccess, CompanyMembershipPermissionCatalog.LegacyTokens);
        Assert.Contains(CompanyMembershipPermissionCatalog.CompanyAccountingSettings, CompanyMembershipPermissionCatalog.LegacyTokens);
        Assert.Contains(CompanyMembershipPermissionCatalog.CompanyBookGovernance, CompanyMembershipPermissionCatalog.LegacyTokens);
        Assert.Contains(CompanyMembershipPermissionCatalog.Reconciliation, CompanyMembershipPermissionCatalog.LegacyTokens);
        Assert.Equal(8, CompanyMembershipPermissionCatalog.LegacyTokens.Count);
    }

    [Fact]
    public void Catalog_adds_fine_grained_tokens()
    {
        Assert.Contains("ar.invoice.create", CompanyMembershipPermissionCatalog.FineGrainedTokens);
        Assert.Contains("task.view", CompanyMembershipPermissionCatalog.FineGrainedTokens);
        Assert.Contains("settings.permissions.assign", CompanyMembershipPermissionCatalog.FineGrainedTokens);
        Assert.Contains("gl.period.close", CompanyMembershipPermissionCatalog.FineGrainedTokens);
    }

    [Fact]
    public void Catalog_AllTokens_is_LegacyTokens_plus_FineGrainedTokens_without_overlap()
    {
        var legacy = CompanyMembershipPermissionCatalog.LegacyTokens.ToHashSet(StringComparer.Ordinal);
        var fine = CompanyMembershipPermissionCatalog.FineGrainedTokens.ToHashSet(StringComparer.Ordinal);
        Assert.False(legacy.Overlaps(fine), "legacy + fine-grained must not share tokens");
        Assert.Equal(legacy.Count + fine.Count, CompanyMembershipPermissionCatalog.AllTokens.Count);
    }

    [Fact]
    public void NormalizeTokens_still_accepts_legacy_token()
    {
        var normalized = CompanyMembershipPermissionCatalog.NormalizeTokens(new[] { "ar" });
        Assert.Equal(new[] { "ar" }, normalized);
    }

    [Fact]
    public void NormalizeTokens_accepts_fine_grained_token()
    {
        var normalized = CompanyMembershipPermissionCatalog.NormalizeTokens(new[] { "ar.invoice.create" });
        Assert.Equal(new[] { "ar.invoice.create" }, normalized);
    }

    [Fact]
    public void NormalizeTokens_rejects_unknown_token()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CompanyMembershipPermissionCatalog.NormalizeTokens(new[] { "task.invent.something" }));
    }
}
