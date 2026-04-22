using Modules.CompanyAccess.SessionContext;
using Web.Shell.Services;
using Web.Shell.State;

namespace Web.Shell.Adapters;

public sealed class WebShellCompanyAccessShellSession(
    WebShellState shellState,
    WebShellBusinessSessionClient sessionClient,
    WebShellSessionExpirationCoordinator sessionExpirationCoordinator) : ICompanyAccessShellSession
{
    public Guid CurrentUserId => shellState.CurrentUserId;

    public Guid ActiveCompanyId => shellState.ActiveCompany.Id;

    public bool AreWritesBlocked => shellState.AreWritesBlocked;

    public string WriteBlockMessage => shellState.WriteBlockMessage;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await shellState.EnsureHydratedAsync(cancellationToken);
        var probe = await sessionClient.ProbeContextAsync(cancellationToken);
        if (await sessionExpirationCoordinator.HandleProbeAsync(probe, cancellationToken))
        {
            return;
        }
    }
}
