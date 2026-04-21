using System.Net.Http.Json;
using Citus.Ui.Shared.Business;
using Web.Shell.State;

namespace Web.Shell.Services;

public sealed class WebShellBusinessSessionClient(
    HttpClient httpClient,
    WebShellState shellState,
    ILogger<WebShellBusinessSessionClient> logger)
{
    public const string AuthenticationRequiredError = "Business sign-in is required.";

    public async Task<BusinessSessionContextSummary?> GetContextAsync(CancellationToken cancellationToken = default)
    {
        var probe = await ProbeContextAsync(cancellationToken);
        return probe.Context;
    }

    public async Task<WebShellSessionContextProbeResult> ProbeContextAsync(CancellationToken cancellationToken = default)
    {
        if (!shellState.IsAuthenticated || string.IsNullOrWhiteSpace(shellState.SessionToken))
        {
            return new WebShellSessionContextProbeResult
            {
                RequiresSignIn = true,
                ErrorMessage = AuthenticationRequiredError
            };
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/api/business/session/context");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new WebShellSessionContextProbeResult
                {
                    Context = await response.Content.ReadFromJsonAsync<BusinessSessionContextSummary>(cancellationToken)
                };
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return new WebShellSessionContextProbeResult
                {
                    RequiresSignIn = true,
                    ErrorMessage = AuthenticationRequiredError
                };
            }

            logger.LogWarning("Unable to load Web.Shell business session context. HTTP {StatusCode}.", response.StatusCode);
            return new WebShellSessionContextProbeResult
            {
                ErrorMessage = await ReadErrorAsync(response, cancellationToken)
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load the Web.Shell business session context probe.");
            return new WebShellSessionContextProbeResult
            {
                ErrorMessage = "Unable to reach the business session context API."
            };
        }
    }

    public async Task<(WebShellBusinessSignInResponse? Result, string? Error)> SignInAsync(
        string login,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "/api/business/session/sign-in",
                new
                {
                    login,
                    password
                },
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<WebShellBusinessSignInResponse>(cancellationToken), null);
            }

            return (null, await ReadErrorAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to sign in to the Web.Shell business session API.");
            return (null, "Unable to reach the business sign-in API.");
        }
    }

    public async Task<(WebShellBusinessSignInResponse? Result, string? Error)> CompleteSecondFactorAsync(
        Guid challengeId,
        string verificationCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "/api/business/session/mfa/complete",
                new
                {
                    challengeId,
                    verificationCode
                },
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<WebShellBusinessSignInResponse>(cancellationToken), null);
            }

            return (null, await ReadErrorAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to complete the Web.Shell business MFA challenge.");
            return (null, "Unable to reach the business MFA verification API.");
        }
    }

    public async Task<WebShellBusinessSessionStateResponse?> ResumeSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/api/business/session", sessionToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Business session resume returned HTTP {StatusCode}.", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<WebShellBusinessSessionStateResponse>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to resume the Web.Shell business session.");
            return null;
        }
    }

    public async Task<(WebShellBusinessSessionStateResponse? Result, string? Error)> SwitchActiveCompanyAsync(
        string sessionToken,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return (null, "Business session token is required.");
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Put, "/api/business/session/active-company", sessionToken);
            request.Content = JsonContent.Create(new
            {
                companyId
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<WebShellBusinessSessionStateResponse>(cancellationToken), null);
            }

            return (null, await ReadErrorAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to switch the Web.Shell active company.");
            return (null, "Unable to reach the business session switch API.");
        }
    }

    public async Task SignOutAsync(
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return;
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Post, "/api/business/session/sign-out", sessionToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Business sign-out returned HTTP {StatusCode}.", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to sign out the Web.Shell business session.");
        }
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string requestUri,
        string? sessionToken = null)
    {
        var request = new HttpRequestMessage(method, requestUri);
        var token = string.IsNullOrWhiteSpace(sessionToken)
            ? shellState.SessionToken
            : sessionToken.Trim();

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Add(BusinessAuthHeaderNames.SessionToken, token);
        }

        return request;
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error.Error;
            }
        }
        catch (Exception)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var plainTextSummary = SummarizePlainTextError(text);
            if (!string.IsNullOrWhiteSpace(plainTextSummary))
            {
                return $"Business session request returned HTTP {(int)response.StatusCode}: {plainTextSummary}";
            }
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return AuthenticationRequiredError;
        }

        return $"Business session request returned HTTP {(int)response.StatusCode}.";
    }

    private static string SummarizePlainTextError(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var summary = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        return summary.Length <= 180
            ? summary
            : summary[..180] + "...";
    }

    private sealed record ErrorResponse(string Error);
}
