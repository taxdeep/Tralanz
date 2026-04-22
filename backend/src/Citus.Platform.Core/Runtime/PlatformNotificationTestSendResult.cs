namespace Citus.Platform.Core.Runtime;

public sealed record class PlatformNotificationTestSendResult
{
    public bool Succeeded { get; init; }

    public string ProviderKey { get; init; } = string.Empty;

    public string Destination { get; init; } = string.Empty;

    public string FailureMessage { get; init; } = string.Empty;

    public PlatformNotificationReadinessReport Readiness { get; init; } = new();
}
