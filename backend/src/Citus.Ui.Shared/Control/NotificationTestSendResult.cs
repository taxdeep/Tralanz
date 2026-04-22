namespace Citus.Ui.Shared.Control;

public sealed record class NotificationTestSendResult
{
    public bool Succeeded { get; init; }

    public string ProviderKey { get; init; } = string.Empty;

    public string Destination { get; init; } = string.Empty;

    public string FailureMessage { get; init; } = string.Empty;

    public NotificationReadinessSummary Readiness { get; init; } = new();
}
