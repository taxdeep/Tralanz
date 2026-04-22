namespace Citus.Platform.Core.Runtime;

public sealed record class PlatformNotificationReadinessReport
{
    public bool ConfigPresent { get; init; }

    public string TestStatus { get; init; } = "untested";

    public DateTimeOffset? LastTestedAtUtc { get; init; }

    public bool VerificationReady { get; init; }

    public bool IsVerificationDeliveryReady { get; init; }

    public string BlockingReason { get; init; } = string.Empty;

    public string ConfigurationError { get; init; } = string.Empty;
}
