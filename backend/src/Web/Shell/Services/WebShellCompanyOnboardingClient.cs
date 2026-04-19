using System.Net.Http.Json;

namespace Web.Shell.Services;

public sealed class WebShellCompanyOnboardingClient(HttpClient httpClient, ILogger<WebShellCompanyOnboardingClient> logger)
{
    public async Task<WebShellAuthenticatedApiResult<WebShellCompanyOnboardingSummary>> GetAsync(
        CancellationToken cancellationToken = default) =>
        await SendAsync<WebShellCompanyOnboardingSummary>(HttpMethod.Get, "/api/company/onboarding/summary", null, cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<WebShellCompanyOnboardingSummary>> AcknowledgeAsync(
        CancellationToken cancellationToken = default) =>
        await SendAsync<WebShellCompanyOnboardingSummary>(HttpMethod.Post, "/api/company/onboarding/acknowledge", new { }, cancellationToken);

    private async Task<WebShellAuthenticatedApiResult<T>> SendAsync<T>(
        HttpMethod method,
        string requestUri,
        object? payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, requestUri);
            if (payload is not null)
            {
                request.Content = JsonContent.Create(payload);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
                return value is null
                    ? WebShellAuthenticatedApiResult<T>.Failure("The onboarding API returned an empty response.")
                    : WebShellAuthenticatedApiResult<T>.Success(value);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<T>.RequiresAuthentication(WebShellBusinessSessionClient.AuthenticationRequiredError);
            }

            return WebShellAuthenticatedApiResult<T>.Failure(await ReadErrorAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to reach the company onboarding API at {RequestUri}.", requestUri);
            return WebShellAuthenticatedApiResult<T>.Failure("Unable to reach the company onboarding API.");
        }
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ErrorPayload>(cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload?.Error))
            {
                return payload.Error;
            }
        }
        catch
        {
            // Best effort only.
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(raw)
            ? $"Company onboarding request returned HTTP {(int)response.StatusCode}."
            : raw;
    }

    private sealed record ErrorPayload(string Error);
}
