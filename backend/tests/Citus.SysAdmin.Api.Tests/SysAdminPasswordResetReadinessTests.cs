using Citus.Platform.Core.Runtime;

namespace Citus.SysAdmin.Api.Tests;

public sealed class SysAdminPasswordResetReadinessTests
{
    [Fact]
    public void NotificationReadiness_IsVerificationDeliveryReady_WhenAllConditionsPass()
    {
        var state = new PlatformNotificationReadinessState
        {
            ConfigPresent = true,
            TestStatus = "passed",
            VerificationReady = true
        };

        Assert.True(state.IsVerificationDeliveryReady);
        Assert.Equal(string.Empty, state.GetBlockingReason());
    }

    [Fact]
    public void NotificationReadiness_Blocks_WhenConfigMissing()
    {
        var state = new PlatformNotificationReadinessState
        {
            ConfigPresent = false,
            TestStatus = "passed",
            VerificationReady = true
        };

        Assert.False(state.IsVerificationDeliveryReady);
        Assert.Equal("Notification configuration is missing.", state.GetBlockingReason());
    }

    [Fact]
    public void NotificationReadiness_Blocks_WhenTestStatusNotPassed()
    {
        var state = new PlatformNotificationReadinessState
        {
            ConfigPresent = true,
            TestStatus = "failed",
            VerificationReady = true
        };

        Assert.False(state.IsVerificationDeliveryReady);
        Assert.Equal("Notification test status is not passed.", state.GetBlockingReason());
    }

    [Fact]
    public void NotificationReadiness_Blocks_WhenVerificationFlagIsFalse()
    {
        var state = new PlatformNotificationReadinessState
        {
            ConfigPresent = true,
            TestStatus = "passed",
            VerificationReady = false
        };

        Assert.False(state.IsVerificationDeliveryReady);
        Assert.Equal("Verification delivery is not marked ready.", state.GetBlockingReason());
    }
}
