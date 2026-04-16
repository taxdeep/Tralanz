namespace Citus.Platform.Core.Accounts;

public sealed record class PlatformProfileChangeRequestResult
{
    public string ChangeType { get; init; } = string.Empty;

    public string MaskedDestination { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public PlatformAccountProfileSummary Profile { get; init; } = new();
}
