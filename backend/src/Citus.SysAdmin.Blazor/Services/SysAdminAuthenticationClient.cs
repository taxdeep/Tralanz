using Citus.Ui.Shared.Control;
using System.Text.Json;

namespace Citus.SysAdmin.Blazor.Services;

public sealed class SysAdminAuthenticationClient(HttpClient httpClient, ILogger<SysAdminAuthenticationClient> logger)
{
    public async Task<SetupStatusResponse?> GetSetupStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("auth/setup", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SysAdmin setup status returned non-success status code {StatusCode}.", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SetupStatusResponse>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read SysAdmin setup status.");
            return null;
        }
    }

    public async Task<SignInResponse?> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "auth/login",
                new SignInRequest
                {
                    Email = email,
                    Password = password
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SysAdmin login returned non-success status code {StatusCode}.", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SignInResponse>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to sign in to SysAdmin API.");
            return null;
        }
    }

    public async Task<CommandOutcome> ProvisionFirstAccountAsync(
        string email,
        string displayName,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "auth/setup/first-account",
                new FirstAccountRequest
                {
                    Email = email,
                    DisplayName = displayName,
                    Password = password
                },
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new CommandOutcome(true, string.Empty);
            }

            return new CommandOutcome(false, await ReadMessageAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to provision first SysAdmin account.");
            return new CommandOutcome(false, "Unable to provision the first SysAdmin account.");
        }
    }

    public async Task<SysAdminAuthSessionSummary?> ResumeSessionAsync(
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
            request.Headers.Add(SysAdminAuthConstants.SessionHeaderName, sessionToken.Trim());

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "SysAdmin session resume returned non-success status code {StatusCode}.",
                    response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SysAdminAuthSessionSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to resume SysAdmin session.");
            return null;
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
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/logout");
            request.Headers.Add(SysAdminAuthConstants.SessionHeaderName, sessionToken.Trim());

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "SysAdmin logout returned non-success status code {StatusCode}.",
                    response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to sign out from SysAdmin API.");
        }
    }

    public async Task<CommandOutcome> RotateSecretAsync(
        string sessionToken,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return new CommandOutcome(false, "SysAdmin session token is required.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/rotate-secret");
            request.Headers.Add(SysAdminAuthConstants.SessionHeaderName, sessionToken.Trim());
            request.Content = JsonContent.Create(new RotateSecretRequest
            {
                CurrentPassword = currentPassword,
                NewPassword = newPassword
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new CommandOutcome(true, string.Empty);
            }

            return new CommandOutcome(false, await ReadMessageAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to rotate SysAdmin secret.");
            return new CommandOutcome(false, "Unable to rotate the SysAdmin secret.");
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"Request failed with status code {(int)response.StatusCode}.";
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? $"Request failed with status code {(int)response.StatusCode}.";
            }
        }
        catch (JsonException)
        {
            return content;
        }

        return content;
    }

    private sealed class SignInRequest
    {
        public string Email { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;
    }

    private sealed class FirstAccountRequest
    {
        public string Email { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;
    }

    private sealed class RotateSecretRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;

        public string NewPassword { get; set; } = string.Empty;
    }

    public sealed class SignInResponse
    {
        public string SessionToken { get; set; } = string.Empty;

        public SysAdminAuthSessionSummary Session { get; set; } = new();
    }

    public sealed class SetupStatusResponse
    {
        public int AccountCount { get; set; }

        public bool HasAnyAccount { get; set; }

        public bool SetupRequired { get; set; }

        public bool BootstrapSeedingEnabled { get; set; }

        public bool BootstrapSeedingActive { get; set; }

        public string BootstrapEmailHint { get; set; } = string.Empty;
    }

    public sealed record CommandOutcome(bool Succeeded, string Message);
}
