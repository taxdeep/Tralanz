using Citus.Ui.Shared.Business;
using System.Text.Json;

namespace Citus.Business.Blazor.Services;

public sealed class BusinessAuthenticationClient(
    HttpClient httpClient,
    ILogger<BusinessAuthenticationClient> logger)
{
    public async Task<SignInResponse> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return SignInResponse.Failed("Email and password are required.");
        }

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "auth/login",
                new SignInRequest { Email = email, Password = password },
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<SignInResponse>(cancellationToken);
                if (payload is not null && !string.IsNullOrWhiteSpace(payload.SessionToken))
                {
                    payload.Succeeded = true;
                    return payload;
                }

                return SignInResponse.Failed("The Accounting API returned an empty session response.");
            }

            var message = await ReadMessageAsync(response, cancellationToken);
            return SignInResponse.Failed(message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Accounting API unreachable.");
            return SignInResponse.Failed("Sign in is temporarily unavailable. Please try again in a moment.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to sign in to the Accounting API.");
            return SignInResponse.Failed("Unable to sign in. Please try again.");
        }
    }

    public async Task<BusinessAuthSessionSummary?> ResumeSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "auth/session");
            request.Headers.Add(BusinessAuthHeaderNames.SessionToken, sessionToken.Trim());

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<BusinessAuthSessionSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to resume the business session.");
            return null;
        }
    }

    /// <summary>
    /// Reads the per-user profile override (display name today). Returns
    /// <c>null</c> if the API is unreachable or the user has no override —
    /// the caller should fall back to whatever the auth summary says.
    /// </summary>
    public async Task<UserProfileSnapshot?> GetProfileOverrideAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("auth/me/profile", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<UserProfileSnapshot>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read user profile override.");
            return null;
        }
    }

    /// <summary>
    /// Persists a new display name for the currently-signed-in user.
    /// Returns <c>true</c> + the saved value on success; failure shape
    /// carries a user-displayable message.
    /// </summary>
    public async Task<UpdateDisplayNameOutcome> UpdateDisplayNameAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return new UpdateDisplayNameOutcome(false, null, "Display name is required.");
        }

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "auth/me/display-name",
                new { displayName = displayName.Trim() },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var message = await ReadMessageAsync(response, cancellationToken);
                return new UpdateDisplayNameOutcome(false, null, message);
            }

            var payload = await response.Content.ReadFromJsonAsync<UserProfileSnapshot>(cancellationToken);
            return new UpdateDisplayNameOutcome(true, payload?.DisplayName, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to update display name.");
            return new UpdateDisplayNameOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    /// <summary>
    /// M17 (AUDIT_2026-05-20 P2-4): switch the session's active
    /// company. The server updates
    /// <c>business_sessions.active_company_id</c> on the row keyed by
    /// the current session token, then returns the refreshed session
    /// summary. The caller MUST apply the returned summary to
    /// ShellState BEFORE issuing further requests — otherwise the
    /// header carries the new company id but the DB still references
    /// the old one, and the route guard's M17 bind check 401s.
    ///
    /// The request itself is safe to make on the OLD header because
    /// /auth/switch-active-company lives outside the route-guarded
    /// /accounting prefix; the server reads the new company id from
    /// the request body, not the header.
    /// </summary>
    public async Task<SwitchActiveCompanyOutcome> SwitchActiveCompanyAsync(
        CompanyId activeCompanyId,
        CancellationToken cancellationToken = default)
    {
        if (activeCompanyId.Value is null)
        {
            return new SwitchActiveCompanyOutcome(false, null, "Active company id is required.");
        }

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "auth/switch-active-company",
                new { activeCompanyId = activeCompanyId.Value },
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var summary = await response.Content.ReadFromJsonAsync<BusinessAuthSessionSummary>(cancellationToken);
                return summary is null
                    ? new SwitchActiveCompanyOutcome(false, null, "The Accounting API returned an empty response.")
                    : new SwitchActiveCompanyOutcome(true, summary, null);
            }

            var message = await ReadMessageAsync(response, cancellationToken);
            return new SwitchActiveCompanyOutcome(false, null, message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Accounting API unreachable for switch-active-company.");
            return new SwitchActiveCompanyOutcome(false, null, "Switch is temporarily unavailable. Please try again.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to switch active company.");
            return new SwitchActiveCompanyOutcome(false, null, "Unable to switch active company. Please try again.");
        }
    }

    public async Task SignOutAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/logout");
            request.Headers.Add(BusinessAuthHeaderNames.SessionToken, sessionToken.Trim());

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Business logout returned non-success status code {StatusCode}.",
                    response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to sign out from the Accounting API.");
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"Sign in failed with status code {(int)response.StatusCode}.";
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? content;
            }
        }
        catch (JsonException)
        {
            // fall through and return the raw payload
        }

        return content;
    }

    public async Task<PasswordResetOutcome> RequestForgotPasswordAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "auth/forgot-password",
                new { email },
                cancellationToken);

            // The endpoint returns the generic "if an account exists,
            // a link has been sent" message either way; just surface
            // whatever the server included.
            var body = await response.Content.ReadFromJsonAsync<PasswordResetAck>(cancellationToken);
            return new PasswordResetOutcome(
                Acknowledged: response.IsSuccessStatusCode,
                Message: body?.Message ?? "If an account matches that email, a reset link has been sent.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Forgot-password request failed.");
            return new PasswordResetOutcome(
                Acknowledged: false,
                Message: "Could not reach the server. Please try again in a moment.");
        }
    }

    public async Task<PasswordResetOutcome> RedeemPasswordResetAsync(
        string token,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "auth/reset-password",
                new { token, newPassword },
                cancellationToken);

            var body = await response.Content.ReadFromJsonAsync<PasswordResetAck>(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new PasswordResetOutcome(true, body?.Message ?? "Password updated.");
            }
            return new PasswordResetOutcome(false, body?.Message ?? "Could not reset the password.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Password reset redeem failed.");
            return new PasswordResetOutcome(false, "Could not reach the server. Please try again in a moment.");
        }
    }

    private sealed record PasswordResetAck(bool Ok, string? Code, string? Message);

    private sealed class SignInRequest
    {
        public string Email { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;
    }

    public sealed class SignInResponse
    {
        public bool Succeeded { get; set; }

        public string SessionToken { get; set; } = string.Empty;

        public BusinessAuthSessionSummary Session { get; set; } = new();

        public string Message { get; set; } = string.Empty;

        public static SignInResponse Failed(string message) => new()
        {
            Succeeded = false,
            Message = message
        };
    }
}

/// <summary>What the API returns from <c>GET /accounting/auth/me/profile</c>.</summary>
public sealed class UserProfileSnapshot
{
    public UserId UserId { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed record UpdateDisplayNameOutcome(bool Succeeded, string? DisplayName, string? ErrorMessage);

/// <summary>
/// M17: outcome of a switch-active-company round-trip. On success
/// <see cref="Session"/> carries the server's refreshed summary; on
/// failure <see cref="ErrorMessage"/> carries a user-displayable
/// message (typically "the selected company is not available in
/// this business session" or a transient connectivity issue).
/// </summary>
public sealed record SwitchActiveCompanyOutcome(
    bool Succeeded,
    BusinessAuthSessionSummary? Session,
    string? ErrorMessage);

public sealed record PasswordResetOutcome(bool Acknowledged, string Message)
{
    /// <summary>True for the redeem path when the password was updated.</summary>
    public bool Succeeded => Acknowledged;
}
