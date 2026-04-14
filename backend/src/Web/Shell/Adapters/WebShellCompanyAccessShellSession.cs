using Modules.CompanyAccess.SessionContext;
using Web.Shell.State;

namespace Web.Shell.Adapters;

public sealed class WebShellCompanyAccessShellSession(WebShellState shellState) : ICompanyAccessShellSession
{
    public Guid CurrentUserId => shellState.CurrentUserId;

    public Guid ActiveCompanyId => shellState.ActiveCompany.Id;
}
