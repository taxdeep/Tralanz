using Modules.CompanyAccess.Memberships;

namespace Citus.SysAdmin.Api.Tests;

public class CompanyMembershipPermissionLegacyExpansionTests
{
    [Fact]
    public void Ar_legacy_expands_to_AR_fine_grained_set()
    {
        var expanded = CompanyMembershipPermissionLegacyExpansion.Expand(new[] { "ar" });

        Assert.Contains("ar", expanded); // legacy token preserved
        Assert.Contains("ar.invoice.view", expanded);
        Assert.Contains("ar.invoice.create", expanded);
        Assert.Contains("ar.customer.view", expanded);
        Assert.Contains("ar.aging.view", expanded);
        // Sanity: did NOT leak AP / GL tokens
        Assert.DoesNotContain("ap.bill.view", expanded);
        Assert.DoesNotContain("gl.journal.view", expanded);
    }

    [Fact]
    public void Approve_legacy_expands_to_three_posting_permissions()
    {
        var expanded = CompanyMembershipPermissionLegacyExpansion.Expand(new[] { "approve" });

        Assert.Contains("approve", expanded);
        Assert.Contains("ar.invoice.post", expanded);
        Assert.Contains("ap.bill.post", expanded);
        Assert.Contains("gl.journal.post", expanded);
    }

    [Fact]
    public void CompanyBookGovernance_legacy_expands_to_settings_meta_permissions()
    {
        var expanded = CompanyMembershipPermissionLegacyExpansion.Expand(new[] { "company_book_governance" });

        Assert.Contains("settings.permissions.assign", expanded);
        Assert.Contains("settings.modules.toggle", expanded);
        Assert.Contains("gl.period.close", expanded);
    }

    [Fact]
    public void Expand_is_idempotent()
    {
        var once = CompanyMembershipPermissionLegacyExpansion.Expand(new[] { "ar", "ap", "reports" });
        var twice = CompanyMembershipPermissionLegacyExpansion.Expand(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void NeedsExpansion_returns_false_after_one_round()
    {
        var initial = new[] { "ar", "ap" };
        Assert.True(CompanyMembershipPermissionLegacyExpansion.NeedsExpansion(initial));

        var expanded = CompanyMembershipPermissionLegacyExpansion.Expand(initial);
        Assert.False(CompanyMembershipPermissionLegacyExpansion.NeedsExpansion(expanded));
    }

    [Fact]
    public void NeedsExpansion_false_for_pure_fine_grained_set()
    {
        Assert.False(CompanyMembershipPermissionLegacyExpansion.NeedsExpansion(
            new[] { "ar.invoice.view", "task.view" }));
    }

    [Fact]
    public void Expand_unknown_tokens_pass_through_untouched()
    {
        var expanded = CompanyMembershipPermissionLegacyExpansion.Expand(new[] { "future.unknown.token" });
        Assert.Equal(new[] { "future.unknown.token" }, expanded);
    }
}
