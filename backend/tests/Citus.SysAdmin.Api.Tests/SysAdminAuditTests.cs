using Citus.Platform.Core.Runtime;

namespace Citus.SysAdmin.Api.Tests;

public sealed class SysAdminAuditTests
{
    [Theory]
    [InlineData("company_status_changed", "Company Status Changed")]
    [InlineData("account_status_changed", "Account Status Changed")]
    [InlineData("password_reset_requested", "Password Reset Requested")]
    [InlineData("password_reset_dispatched", "Password Reset Delivered")]
    [InlineData("password_reset_dispatch_failed", "Password Reset Delivery Failed")]
    [InlineData("membership_role_changed", "Membership Role Changed")]
    [InlineData("membership_permissions_saved", "Membership Permissions Saved")]
    [InlineData("sysadmin_first_account_created", "First SysAdmin Created")]
    [InlineData("sysadmin_password_rotated", "SysAdmin Secret Rotated")]
    [InlineData("unknown_action", "Governance Event")]
    public void PlatformAuditEvent_GetActionLabel_ReturnsExpectedLabel(string action, string expectedLabel)
    {
        var actual = PlatformAuditEvent.GetActionLabel(action);

        Assert.Equal(expectedLabel, actual);
    }

    [Fact]
    public void PlatformAuditEvent_BuildScopeLabel_ReturnsPlatform_WhenCompanyReferenceMissing()
    {
        var actual = PlatformAuditEvent.BuildScopeLabel(string.Empty, string.Empty);

        Assert.Equal("Platform", actual);
    }

    [Fact]
    public void PlatformAuditEvent_BuildScopeLabel_ReturnsCompanyReference_WhenAvailable()
    {
        var actual = PlatformAuditEvent.BuildScopeLabel("Northwind Studio Ltd.", "EN20260000U");

        Assert.Equal("Northwind Studio Ltd. (EN202600000001)", actual);
    }

    [Fact]
    public void PlatformAuditEvent_BuildPermissionChangeDetail_FormatsAddedAndRemovedTokens()
    {
        var actual = PlatformAuditEvent.BuildPermissionChangeDetail(
            ["ap", "reports"],
            ["approve"]);

        Assert.Equal("+ ap, reports | - approve", actual);
    }

    [Fact]
    public void PlatformAuditEvent_BuildPermissionChangeDetail_ReturnsNeutralMessage_WhenNoNetChangesExist()
    {
        var actual = PlatformAuditEvent.BuildPermissionChangeDetail([], []);

        Assert.Equal("Permission set was saved without net token changes.", actual);
    }
}
