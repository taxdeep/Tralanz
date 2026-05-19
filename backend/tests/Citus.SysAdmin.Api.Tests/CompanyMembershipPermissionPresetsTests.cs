using Modules.CompanyAccess.Memberships;

namespace Citus.SysAdmin.Api.Tests;

public class CompanyMembershipPermissionPresetsTests
{
    [Fact]
    public void Owner_preset_contains_every_catalog_token()
    {
        var owner = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Owner);

        foreach (var token in CompanyMembershipPermissionCatalog.AllTokens)
        {
            Assert.Contains(token, owner);
        }
    }

    [Fact]
    public void Accountant_preset_grants_ar_ap_gl_reports_and_inventory_read()
    {
        var accountant = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Accountant);

        Assert.Contains("ar.invoice.post", accountant);
        Assert.Contains("ap.bill.post", accountant);
        Assert.Contains("gl.journal.post", accountant);
        Assert.Contains("reports.view", accountant);
        Assert.Contains("inventory.item.view", accountant);
        Assert.Contains("inventory.price.view", accountant);
        // Legacy bridges so existing AR/AP/Reports authorization codepaths keep working.
        Assert.Contains("ar", accountant);
        Assert.Contains("ap", accountant);
        Assert.Contains("reports", accountant);
        // Sanity: no settings.*
        Assert.DoesNotContain("settings.permissions.assign", accountant);
        Assert.DoesNotContain("settings.modules.toggle", accountant);
    }

    [Fact]
    public void Sales_preset_grants_invoice_customer_task_inventory_read()
    {
        var sales = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Sales);

        Assert.Contains("ar.invoice.create", sales);
        Assert.Contains("ar.customer.create", sales);
        Assert.Contains("task.create", sales);
        Assert.Contains("task.bill", sales);
        Assert.Contains("inventory.item.view", sales);
        Assert.Contains("inventory.price.view", sales);
        // No posting, no AP, no inventory writes
        Assert.DoesNotContain("ar.invoice.post", sales);
        Assert.DoesNotContain("ap.bill.view", sales);
        Assert.DoesNotContain("inventory.item.edit", sales);
    }

    [Fact]
    public void Bookkeeper_preset_grants_ap_workflow_only()
    {
        var bookkeeper = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Bookkeeper);

        Assert.Contains("ap.bill.post", bookkeeper);
        Assert.Contains("ap.payment.apply", bookkeeper);
        Assert.Contains("ap.vendor.create", bookkeeper);
        Assert.Contains("inventory.item.view", bookkeeper);
        // No AR
        Assert.DoesNotContain("ar.invoice.view", bookkeeper);
        Assert.DoesNotContain("ar.customer.view", bookkeeper);
    }

    [Fact]
    public void Viewer_preset_is_read_only()
    {
        var viewer = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Viewer);

        Assert.All(viewer, token =>
            Assert.True(
                token.EndsWith(".view", StringComparison.Ordinal) ||
                token.EndsWith(".read", StringComparison.Ordinal),
                $"Viewer preset leaked a non-read token: '{token}'."));
        Assert.Contains("ar.invoice.view", viewer);
        Assert.Contains("ap.bill.view", viewer);
        Assert.Contains("gl.journal.view", viewer);
        Assert.Contains("inventory.item.view", viewer);
    }

    [Fact]
    public void TaskOnly_preset_is_minimal()
    {
        var taskOnly = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.TaskOnly);

        Assert.Contains("task.view", taskOnly);
        Assert.Contains("task.create", taskOnly);
        Assert.Contains("task.edit", taskOnly);
        Assert.Contains("task.complete", taskOnly);
        Assert.DoesNotContain("task.bill", taskOnly);
        Assert.DoesNotContain("ar.invoice.create", taskOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("preset.unknown")]
    public void Expand_throws_on_unknown_or_blank_preset(string? input)
    {
        Assert.Throws<InvalidOperationException>(() =>
            CompanyMembershipPermissionPresets.Expand(input!));
    }

    [Fact]
    public void IsKnown_recognizes_each_advertised_preset()
    {
        foreach (var preset in CompanyMembershipPermissionPresets.KnownPresets)
        {
            Assert.True(CompanyMembershipPermissionPresets.IsKnown(preset));
        }
    }

    [Fact]
    public void Every_preset_expansion_is_a_subset_of_the_catalog()
    {
        var catalog = CompanyMembershipPermissionCatalog.AllTokens.ToHashSet(StringComparer.Ordinal);
        foreach (var preset in CompanyMembershipPermissionPresets.KnownPresets)
        {
            var tokens = CompanyMembershipPermissionPresets.Expand(preset);
            foreach (var token in tokens)
            {
                Assert.True(catalog.Contains(token), $"Preset '{preset}' contains unknown token '{token}'.");
            }
        }
    }
}
