using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Citus.Business.Blazor.State;

/// <summary>
/// Bridges the Blazor circuit's IServiceProvider into non-Blazor
/// scopes — most importantly, into <see cref="DelegatingHandler"/>
/// chains served by <see cref="IHttpClientFactory"/>.
///
/// Background: HttpClientFactory pools handler instances inside its
/// own DI scope. A handler with a <c>BusinessShellState</c> ctor
/// dependency captures a fresh <c>BusinessShellState</c> from that
/// pool scope, never the circuit's. Once the user authenticates in
/// the circuit, the captured state stays default-constructed — so
/// every header attached by the handler carries Guid.Empty for
/// UserId / ActiveCompanyId, and every API call lands as
/// "no membership for company 00000000-0000-...".
///
/// The fix follows the MS-documented pattern: a singleton accessor
/// backed by AsyncLocal, set by a <see cref="CircuitHandler"/> at
/// the top of every circuit inbound activity. Inside the handler,
/// resolving a service through <see cref="Services"/> hits the
/// circuit's DI scope and therefore the live BusinessShellState.
/// </summary>
public sealed class CircuitServicesAccessor
{
    private static readonly AsyncLocal<IServiceProvider?> _services = new();

    public IServiceProvider? Services
    {
        get => _services.Value;
        set => _services.Value = value;
    }
}

internal sealed class CircuitServicesAccessorCircuitHandler(
    IServiceProvider services,
    CircuitServicesAccessor accessor) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            accessor.Services = services;
            try
            {
                await next(context);
            }
            finally
            {
                accessor.Services = null;
            }
        };
    }
}
