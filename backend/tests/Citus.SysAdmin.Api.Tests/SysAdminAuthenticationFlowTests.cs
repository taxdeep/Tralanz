using Citus.Platform.Core.Runtime;
using Citus.SysAdmin.Api.Auth;

namespace Citus.SysAdmin.Api.Tests;

public sealed class SysAdminAuthenticationFlowTests
{
    [Fact]
    public void BootstrapOptions_IsActive_InDevelopment_WhenEnabled()
    {
        var options = new SysAdminAuthOptions.BootstrapOptions
        {
            Enabled = true,
            AllowInNonDevelopment = false
        };

        Assert.True(options.IsActive(isDevelopment: true));
    }

    [Fact]
    public void BootstrapOptions_IsInactive_InNonDevelopment_ByDefault()
    {
        var options = new SysAdminAuthOptions.BootstrapOptions
        {
            Enabled = true,
            AllowInNonDevelopment = false
        };

        Assert.False(options.IsActive(isDevelopment: false));
    }

    [Fact]
    public void BootstrapOptions_CanBeExplicitlyEnabled_InNonDevelopment()
    {
        var options = new SysAdminAuthOptions.BootstrapOptions
        {
            Enabled = true,
            AllowInNonDevelopment = true
        };

        Assert.True(options.IsActive(isDevelopment: false));
    }

    [Fact]
    public void SysAdminSetupStatus_RequiresSetup_WhenNoAccountsExist()
    {
        var status = new SysAdminSetupStatus
        {
            AccountCount = 0
        };

        Assert.True(status.SetupRequired);
        Assert.False(status.HasAnyAccount);
    }

    [Fact]
    public void SysAdminSetupStatus_DoesNotRequireSetup_WhenAccountExists()
    {
        var status = new SysAdminSetupStatus
        {
            AccountCount = 1
        };

        Assert.False(status.SetupRequired);
        Assert.True(status.HasAnyAccount);
    }
}
