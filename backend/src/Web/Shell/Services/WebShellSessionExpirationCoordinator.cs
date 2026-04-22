using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Web.Shell.State;

namespace Web.Shell.Services;

public sealed class WebShellSessionExpirationCoordinator(
    WebShellState shellState,
    WebShellBusinessSessionClient sessionClient,
    IJSRuntime jsRuntime,
    NavigationManager navigationManager,
    ISnackbar snackbar)
{
    public async Task<bool> HandleProbeAsync(
        WebShellSessionContextProbeResult probe,
        CancellationToken cancellationToken = default)
    {
        if (probe.Context is not null)
        {
            shellState.ApplyBusinessSessionContext(probe.Context);
            return false;
        }

        if (!probe.RequiresSignIn)
        {
            return false;
        }

        return await ExpireSessionAsync();
    }

    public async Task<bool> HandleAuthenticatedApiResultAsync<T>(
        WebShellAuthenticatedApiResult<T> result,
        CancellationToken cancellationToken = default)
    {
        if (!result.RequiresSignIn)
        {
            return false;
        }

        var probe = await sessionClient.ProbeContextAsync(cancellationToken);
        if (probe.Context is not null)
        {
            shellState.ApplyBusinessSessionContext(probe.Context);
            return false;
        }

        if (!probe.RequiresSignIn)
        {
            return false;
        }

        return await ExpireSessionAsync();
    }

    private async Task<bool> ExpireSessionAsync()
    {
        await jsRuntime.InvokeVoidAsync("citusBusinessAuth.clearSessionToken");
        shellState.ClearAuthenticatedSession();
        snackbar.Add("Your business session has expired. Please sign in again.", Severity.Warning);
        navigationManager.NavigateTo("login", replace: true);
        return true;
    }
}
