using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Web.Shell.State;

namespace Web.Shell.Services;

public sealed class WebShellSessionExpirationCoordinator(
    WebShellState shellState,
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

    public Task<bool> HandleAuthenticatedApiResultAsync<T>(
        WebShellAuthenticatedApiResult<T> result,
        CancellationToken cancellationToken = default) =>
        result.RequiresSignIn
            ? ExpireSessionAsync()
            : Task.FromResult(false);

    private async Task<bool> ExpireSessionAsync()
    {
        await jsRuntime.InvokeVoidAsync("citusBusinessAuth.clearSessionToken");
        shellState.ClearAuthenticatedSession();
        snackbar.Add("Your business session has expired. Please sign in again.", Severity.Warning);
        navigationManager.NavigateTo("login", replace: true);
        return true;
    }
}
