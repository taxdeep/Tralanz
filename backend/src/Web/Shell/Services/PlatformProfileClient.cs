using System.Net;
using System.Net.Http.Json;
using Citus.Platform.Core.Accounts;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Control;
using Web.Shell.State;

namespace Web.Shell.Services;

public sealed class PlatformProfileClient(
    HttpClient httpClient,
    WebShellState shellState,
    ILogger<PlatformProfileClient> logger)
{
    public const string AuthenticationRequiredError = "Business sign-in is required.";

    public Task<(PlatformAccountProfileSummary? Result, string? Error)> GetAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformAccountProfileSummary>(
            static request => request.Method = HttpMethod.Get,
            "/api/platform/profile",
            cancellationToken);

    public async Task<(NotificationReadinessSummary? Result, string? Error)> GetNotificationReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        if (!shellState.IsAuthenticated || string.IsNullOrWhiteSpace(shellState.SessionToken))
        {
            return (null, AuthenticationRequiredError);
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/api/platform/notification-readiness");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<NotificationReadinessSummary>(cancellationToken), null);
            }

            return (null, await ReadErrorAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load platform notification readiness.");
            return (null, "Unable to reach the platform notification readiness API.");
        }
    }

    public Task<(PlatformAccountProfileSummary? Result, string? Error)> SaveDisplayNameAsync(
        string displayName,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformAccountProfileSummary>(
            request => request.Method = HttpMethod.Put,
            "/api/platform/profile/display-name",
            cancellationToken,
            new SaveDisplayNameRequest(displayName));

    public Task<(PlatformProfileChangeRequestResult? Result, string? Error)> RequestEmailChangeAsync(
        string newEmail,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformProfileChangeRequestResult>(
            request => request.Method = HttpMethod.Post,
            "/api/platform/profile/email-change/request",
            cancellationToken,
            new RequestEmailChangeRequest(newEmail));

    public Task<(PlatformProfileChangeConfirmationResult? Result, string? Error)> ConfirmEmailChangeAsync(
        string verificationCode,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformProfileChangeConfirmationResult>(
            request => request.Method = HttpMethod.Post,
            "/api/platform/profile/email-change/confirm",
            cancellationToken,
            new ConfirmVerificationRequest(verificationCode));

    public Task<(PlatformProfileChangeRequestResult? Result, string? Error)> RequestPasswordChangeAsync(
        string newPassword,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformProfileChangeRequestResult>(
            request => request.Method = HttpMethod.Post,
            "/api/platform/profile/password-change/request",
            cancellationToken,
            new RequestPasswordChangeRequest(newPassword));

    public Task<(PlatformProfileChangeConfirmationResult? Result, string? Error)> ConfirmPasswordChangeAsync(
        string verificationCode,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformProfileChangeConfirmationResult>(
            request => request.Method = HttpMethod.Post,
            "/api/platform/profile/password-change/confirm",
            cancellationToken,
            new ConfirmVerificationRequest(verificationCode));

    private async Task<(TResult? Result, string? Error)> SendAsync<TResult>(
        Action<HttpRequestMessage> configureRequest,
        string requestUri,
        CancellationToken cancellationToken,
        object? payload = null)
        where TResult : class
    {
        if (!shellState.IsAuthenticated || string.IsNullOrWhiteSpace(shellState.SessionToken))
        {
            return (null, AuthenticationRequiredError);
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Get, requestUri);
            configureRequest(request);
            if (payload is not null)
            {
                request.Content = JsonContent.Create(payload);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<TResult>(cancellationToken), null);
            }

            return (null, await ReadErrorAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to complete platform profile request against {RequestUri}.", requestUri);
            return (null, "Unable to reach the platform profile API.");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string requestUri)
    {
        var request = new HttpRequestMessage(method, requestUri);
        if (!string.IsNullOrWhiteSpace(shellState.SessionToken))
        {
            request.Headers.Add(BusinessAuthHeaderNames.SessionToken, shellState.SessionToken);
        }

        return request;
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return AuthenticationRequiredError;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "Platform account was not found.";
        }

        var error = await response.Content.ReadFromJsonAsync<PlatformProfileApiError>(cancellationToken);
        if (!string.IsNullOrWhiteSpace(error?.Error))
        {
            return error.Error;
        }

        return $"Platform profile request returned HTTP {(int)response.StatusCode}.";
    }

    private sealed record SaveDisplayNameRequest(string DisplayName);

    private sealed record RequestEmailChangeRequest(string NewEmail);

    private sealed record RequestPasswordChangeRequest(string NewPassword);

    private sealed record ConfirmVerificationRequest(string VerificationCode);

    private sealed record PlatformProfileApiError(string Error);
}
