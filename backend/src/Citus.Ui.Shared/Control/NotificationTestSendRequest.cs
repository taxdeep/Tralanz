namespace Citus.Ui.Shared.Control;

public sealed record class NotificationTestSendRequest
{
    public string Destination { get; init; } = string.Empty;

    public string RecipientDisplayName { get; init; } = string.Empty;
}
