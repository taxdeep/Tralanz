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
    public Guid UserId { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed record UpdateDisplayNameOutcome(bool Succeeded, string? DisplayName, string? ErrorMessage);
