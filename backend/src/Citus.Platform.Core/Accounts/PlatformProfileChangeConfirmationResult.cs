namespace Citus.Platform.Core.Accounts;

public sealed record class PlatformProfileChangeConfirmationResult
{
    public string ChangeType { get; init; } = string.Empty;

    public DateTimeOffset ConfirmedAtUtc { get; init; }

    public PlatformAccountProfileSummary Profile { get; init; } = new();
}
