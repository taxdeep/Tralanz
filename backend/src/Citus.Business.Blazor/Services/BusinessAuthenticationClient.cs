using Citus.Business.Blazor.Configuration;
using Citus.Ui.Shared.Business;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Citus.Business.Blazor.Services;

public sealed class BusinessAuthenticationClient(
    HttpClient httpClient,
    IOptions<AppHostOptions> hostOptions,
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

            // Past behaviour: fall back to a bootstrap (Northwind/Alice) session
            // on 404 so the dev experience worked before /auth/login shipped.
            // That fallback now hides a real misconfiguration — operators who
            // had just provisioned a real owner via the SysAdmin First-Company
            // Wizard kept landing on "Northwind" because /auth/login was 404'ing
            // and the silent fallback masked it. /auth/login is wired now;
            // a 404 here means the API is genuinely missing the endpoint, and
            // a clear error is more useful than a misleading "Welcome, Alice".
            var message = await ReadMessageAsync(response, cancellationToken);
            return SignInResponse.Failed(message);
        }
        catch (HttpRequestException ex)
        {
            // Network-level unreachable (DNS, refused connection, TLS handshake).
            // Keep the bootstrap fallback only here so a fully offline dev box
            // can still demo the shell. Once a real session is needed, the
            // operator will need the API up.
            logger.LogWarning(ex, "Accounting API unreachable; falling back to bootstrap session.");
            return BuildBootstrapResponse();
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

        if (sessionToken.StartsWith(BootstrapSessionPrefix, StringComparison.Ordinal))
        {
            return BuildBootstrapSummary();
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
        if (string.IsNullOrWhiteSpace(sessionToken) ||
            sessionToken.StartsWith(BootstrapSessionPrefix, StringComparison.Ordinal))
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

    private const string BootstrapSessionPrefix = "bootstrap:";

    private SignInResponse BuildBootstrapResponse()
    {
        var summary = BuildBootstrapSummary();
        return new SignInResponse
        {
            Succeeded = true,
            SessionToken = $"{BootstrapSessionPrefix}{Guid.NewGuid():N}",
            Session = summary,
            IsBootstrap = true,
            Message = "Signed in with the local bootstrap user. Wire up the Accounting auth endpoints to use a real session."
        };
    }

    private BusinessAuthSessionSummary BuildBootstrapSummary()
    {
        var bootstrap = hostOptions.Value;
        return new BusinessAuthSessionSummary
        {
            User = new BusinessUserSummary
            {
                Id = bootstrap.BootstrapUserId,
                DisplayName = bootstrap.BootstrapUserDisplayName,
                Email = bootstrap.BootstrapUserEmail,
                Username = bootstrap.BootstrapUsername,
                Roles = bootstrap.BootstrapRoles
            },
            ActiveCompany = new BusinessCompanySummary
            {
                Id = bootstrap.BootstrapCompanyId,
                CompanyCode = bootstrap.BootstrapCompanyCode,
                CompanyName = bootstrap.BootstrapCompanyName,
                BaseCurrencyCode = bootstrap.BootstrapCompanyBaseCurrencyCode,
                MultiCurrencyEnabled = bootstrap.BootstrapCompanyMultiCurrencyEnabled
            },
            AvailableCompanies = new List<BusinessCompanySummary>
            {
                new()
                {
                    Id = bootstrap.BootstrapCompanyId,
                    CompanyCode = bootstrap.BootstrapCompanyCode,
                    CompanyName = bootstrap.BootstrapCompanyName,
                    BaseCurrencyCode = bootstrap.BootstrapCompanyBaseCurrencyCode,
                    MultiCurrencyEnabled = bootstrap.BootstrapCompanyMultiCurrencyEnabled
                }
            }
        };
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

        public bool IsBootstrap { get; set; }

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
