using Citus.Ui.Shared.Business;

namespace Web.Shell.Services;

public sealed record class WebShellBusinessSignInResponse
{
    public string SessionToken { get; init; } = string.Empty;

    public BusinessSessionContextSummary? Context { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public string AuthenticationStage { get; init; } = "authenticated";

    public bool RequiresSecondFactor { get; init; }

    public string? MfaChallengeId { get; init; }

    public IReadOnlyList<string> AvailableSecondFactors { get; init; } = [];
}

public sealed record class WebShellBusinessSessionStateResponse
{
    public BusinessSessionContextSummary Context { get; init; } = new();

    public DateTimeOffset ExpiresAtUtc { get; init; }
}
