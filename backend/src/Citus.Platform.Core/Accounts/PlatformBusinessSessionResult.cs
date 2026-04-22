namespace Citus.Platform.Core.Accounts;

public sealed record class PlatformBusinessSessionResult
{
    public bool Succeeded { get; init; }

    public string SessionToken { get; init; } = string.Empty;

    public Guid UserId { get; init; }

    public Guid ActiveCompanyId { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public string AuthenticationStage { get; init; } = "authenticated";

    public bool RequiresSecondFactor { get; init; }

    public Guid? MfaChallengeId { get; init; }

    public DateTimeOffset? MfaChallengeExpiresAtUtc { get; init; }

    public IReadOnlyList<string> AvailableSecondFactors { get; init; } = [];

    public string FailureCode { get; init; } = string.Empty;

    public string FailureMessage { get; init; } = string.Empty;
}
