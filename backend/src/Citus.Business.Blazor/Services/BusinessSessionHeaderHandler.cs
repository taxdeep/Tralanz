using Citus.Business.Blazor.State;
using Citus.Ui.Shared.Business;

namespace Citus.Business.Blazor.Services;

public sealed class BusinessSessionHeaderHandler(BusinessShellState shellState) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(BusinessSessionHeaderNames.UserId);
        request.Headers.Remove(BusinessSessionHeaderNames.ActiveCompanyId);
        request.Headers.Add(BusinessSessionHeaderNames.UserId, shellState.CurrentUserId.ToString());
        request.Headers.Add(BusinessSessionHeaderNames.ActiveCompanyId, shellState.ActiveCompany.Id.ToString());

        return base.SendAsync(request, cancellationToken);
    }
}
