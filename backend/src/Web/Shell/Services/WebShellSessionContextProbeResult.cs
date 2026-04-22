using Citus.Ui.Shared.Business;

namespace Web.Shell.Services;

public sealed record class WebShellSessionContextProbeResult
{
    public BusinessSessionContextSummary? Context { get; init; }

    public bool RequiresSignIn { get; init; }

    public string? ErrorMessage { get; init; }
}
