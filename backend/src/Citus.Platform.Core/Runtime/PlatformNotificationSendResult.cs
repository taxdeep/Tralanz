namespace Citus.Platform.Core.Runtime;

public sealed record class PlatformNotificationSendResult
{
    public bool Succeeded { get; init; }

    public string ProviderKey { get; init; } = string.Empty;

    public string ExternalReference { get; init; } = string.Empty;

    public string FailureMessage { get; init; } = string.Empty;
}
