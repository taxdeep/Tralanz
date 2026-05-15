using Citus.Business.Blazor.State;
using Citus.Ui.Shared.Business;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Attaches the active business session's user / company headers to every
/// outgoing API request. The handler resolves <see cref="BusinessShellState"/>
/// through <see cref="CircuitServicesAccessor"/> on every send — NOT through
/// constructor injection — because HttpClientFactory's handler pool lives in
/// its own DI scope and would otherwise capture a default-constructed,
/// never-authenticated ShellState. CircuitServicesAccessor's AsyncLocal-backed
/// pointer is set by the matching CircuitHandler at the top of every circuit
/// inbound activity, so any send that happens during a Blazor interaction
/// sees the live circuit state.
/// </summary>
public sealed class BusinessSessionHeaderHandler(CircuitServicesAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(BusinessSessionHeaderNames.UserId);
        request.Headers.Remove(BusinessSessionHeaderNames.ActiveCompanyId);
        request.Headers.Remove(BusinessSessionHeaderNames.LegacyUserId);
        request.Headers.Remove(BusinessSessionHeaderNames.LegacyActiveCompanyId);
        request.Headers.Remove(BusinessAuthHeaderNames.SessionToken);
        request.Headers.Remove(BusinessAuthHeaderNames.LegacySessionToken);

        // Pull ShellState from the circuit's IServiceProvider, not our own.
        // Bootstrap-time calls (before any circuit is active, e.g. during
        // pre-render or before MainLayout's resume completes) fall back to
        // empty headers; the receiving endpoints already 401/403 on missing
        // session, and the caller's catch handlers degrade gracefully.
        var shellState = accessor.Services?.GetService<BusinessShellState>();
        if (shellState is not null)
        {
            if (!string.IsNullOrWhiteSpace(shellState.SessionToken))
            {
                request.Headers.Add(BusinessAuthHeaderNames.SessionToken, shellState.SessionToken);
            }

            request.Headers.Add(BusinessSessionHeaderNames.UserId, shellState.CurrentUserId.ToString());
            request.Headers.Add(BusinessSessionHeaderNames.ActiveCompanyId, shellState.ActiveCompany.Id.ToString());
        }

        return base.SendAsync(request, cancellationToken);
    }
}
