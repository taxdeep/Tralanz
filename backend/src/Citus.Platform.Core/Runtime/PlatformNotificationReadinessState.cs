namespace Citus.Platform.Core.Runtime;

public sealed record class PlatformNotificationReadinessState
{
    public bool ConfigPresent { get; init; }

    public string TestStatus { get; init; } = "untested";

    public DateTimeOffset? LastTestedAtUtc { get; init; }

    public bool VerificationReady { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsVerificationDeliveryReady =>
        ConfigPresent &&
        VerificationReady &&
        string.Equals(TestStatus, "passed", StringComparison.OrdinalIgnoreCase);

    public string GetBlockingReason()
    {
        if (!ConfigPresent)
        {
            return "Notification configuration is missing.";
        }

        if (!string.Equals(TestStatus, "passed", StringComparison.OrdinalIgnoreCase))
        {
            return "Notification test status is not passed.";
        }

        if (!VerificationReady)
        {
            return "Verification delivery is not marked ready.";
        }

        return string.Empty;
    }
}
