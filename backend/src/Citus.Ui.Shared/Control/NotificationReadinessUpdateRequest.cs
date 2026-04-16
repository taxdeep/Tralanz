namespace Citus.Ui.Shared.Control;

public sealed record class NotificationReadinessUpdateRequest
{
    public bool ConfigPresent { get; init; }

    public string TestStatus { get; init; } = "untested";

    public DateTimeOffset? LastTestedAtUtc { get; init; }

    public bool VerificationReady { get; init; }
}
