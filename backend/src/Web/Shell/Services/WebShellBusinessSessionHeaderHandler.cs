using Citus.Ui.Shared.Business;
using Web.Shell.State;

namespace Web.Shell.Services;

public sealed class WebShellBusinessSessionHeaderHandler(WebShellState shellState) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await shellState.EnsureHydratedAsync(cancellationToken);

        request.Headers.Remove(BusinessSessionHeaderNames.UserId);
        request.Headers.Remove(BusinessSessionHeaderNames.ActiveCompanyId);
        request.Headers.Add(BusinessSessionHeaderNames.UserId, shellState.CurrentUserId.ToString());
        request.Headers.Add(BusinessSessionHeaderNames.ActiveCompanyId, shellState.ActiveCompany.Id.ToString());

        return await base.SendAsync(request, cancellationToken);
    }
}
