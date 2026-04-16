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
    public Task<WebShellAuthenticatedApiResult<PlatformAccountProfileSummary>> GetAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformAccountProfileSummary>(
            static request => request.Method = HttpMethod.Get,
            "/api/platform/profile",
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<IReadOnlyList<PlatformMfaTimelineEntry>>> GetMfaTimelineAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<PlatformMfaTimelineEntry>>(
            static request => request.Method = HttpMethod.Get,
            "/api/platform/profile/mfa-timeline",
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<NotificationReadinessSummary>> GetNotificationReadinessAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<NotificationReadinessSummary>(
            static request => request.Method = HttpMethod.Get,
            "/api/platform/notification-readiness",
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<PlatformAccountProfileSummary>> SaveDisplayNameAsync(
        string displayName,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformAccountProfileSummary>(
            request => request.Method = HttpMethod.Put,
            "/api/platform/profile/display-name",
            cancellationToken,
            new SaveDisplayNameRequest(displayName));

    public Task<WebShellAuthenticatedApiResult<PlatformAccountProfileSummary>> SaveMfaModeAsync(
        string mfaMode,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformAccountProfileSummary>(
            request => request.Method = HttpMethod.Put,
            "/api/platform/profile/mfa-mode",
            cancellationToken,
            new SaveMfaModeRequest(mfaMode));

    public Task<WebShellAuthenticatedApiResult<PlatformMfaRecoveryRequestResult>> RequestMfaRecoveryAsync(
        string reason,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformMfaRecoveryRequestResult>(
            request => request.Method = HttpMethod.Post,
            "/api/platform/profile/mfa-recovery/request",
            cancellationToken,
            new RequestMfaRecoveryRequest(reason));

    public Task<WebShellAuthenticatedApiResult<PlatformProfileChangeRequestResult>> RequestEmailChangeAsync(
        string newEmail,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformProfileChangeRequestResult>(
            request => request.Method = HttpMethod.Post,
            "/api/platform/profile/email-change/request",
            cancellationToken,
            new RequestEmailChangeRequest(newEmail));

    public Task<WebShellAuthenticatedApiResult<PlatformProfileChangeConfirmationResult>> ConfirmEmailChangeAsync(
        string verificationCode,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformProfileChangeConfirmationResult>(
            request => request.Method = HttpMethod.Post,
            "/api/platform/profile/email-change/confirm",
            cancellationToken,
            new ConfirmVerificationRequest(verificationCode));

    public Task<WebShellAuthenticatedApiResult<PlatformProfileChangeRequestResult>> RequestPasswordChangeAsync(
        string newPassword,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformProfileChangeRequestResult>(
            request => request.Method = HttpMethod.Post,
            "/api/platform/profile/password-change/request",
            cancellationToken,
            new RequestPasswordChangeRequest(newPassword));

    public Task<WebShellAuthenticatedApiResult<PlatformProfileChangeConfirmationResult>> ConfirmPasswordChangeAsync(
        string verificationCode,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlatformProfileChangeConfirmationResult>(
            request => request.Method = HttpMethod.Post,
            "/api/platform/profile/password-change/confirm",
            cancellationToken,
            new ConfirmVerificationRequest(verificationCode));

    private async Task<WebShellAuthenticatedApiResult<TResult>> SendAsync<TResult>(
        Action<HttpRequestMessage> configureRequest,
        string requestUri,
        CancellationToken cancellationToken,
        object? payload = null)
        where TResult : class
    {
        if (!shellState.IsAuthenticated || string.IsNullOrWhiteSpace(shellState.SessionToken))
        {
            return WebShellAuthenticatedApiResult<TResult>.RequiresAuthentication();
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
                return WebShellAuthenticatedApiResult<TResult>.Success(
                    await response.Content.ReadFromJsonAsync<TResult>(cancellationToken));
            }

            return await ReadErrorAsync<TResult>(response, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to complete platform profile request against {RequestUri}.", requestUri);
            return WebShellAuthenticatedApiResult<TResult>.Failure("Unable to reach the platform profile API.");
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

    private static async Task<WebShellAuthenticatedApiResult<TResult>> ReadErrorAsync<TResult>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where TResult : class
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return WebShellAuthenticatedApiResult<TResult>.RequiresAuthentication();
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return WebShellAuthenticatedApiResult<TResult>.NotFound("Platform account was not found.");
        }

        var error = await response.Content.ReadFromJsonAsync<PlatformProfileApiError>(cancellationToken);
        if (!string.IsNullOrWhiteSpace(error?.Error))
        {
            return WebShellAuthenticatedApiResult<TResult>.Failure(error.Error);
        }

        return WebShellAuthenticatedApiResult<TResult>.Failure(
            $"Platform profile request returned HTTP {(int)response.StatusCode}.");
    }

    private sealed record SaveDisplayNameRequest(string DisplayName);

    private sealed record SaveMfaModeRequest(string MfaMode);

    private sealed record RequestMfaRecoveryRequest(string Reason);

    private sealed record RequestEmailChangeRequest(string NewEmail);

    private sealed record RequestPasswordChangeRequest(string NewPassword);

    private sealed record ConfirmVerificationRequest(string VerificationCode);

    private sealed record PlatformProfileApiError(string Error);
}
