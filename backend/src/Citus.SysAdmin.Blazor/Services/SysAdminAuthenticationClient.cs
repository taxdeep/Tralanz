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

    public async Task<SetupStatusResponse?> SetFirstCompanyDecisionAsync(
        string sessionToken,
        bool createCompanyNow,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/setup/company-decision");
            request.Headers.Add(SysAdminAuthConstants.SessionHeaderName, sessionToken.Trim());
            request.Content = JsonContent.Create(new FirstCompanyDecisionRequest
            {
                CreateCompanyNow = createCompanyNow
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "SysAdmin first-company decision returned non-success status code {StatusCode}.",
                    response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SetupStatusResponse>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to persist SysAdmin first-company setup decision.");
            return null;
        }
    }

    public async Task<FirstCompanyProvisioningResponse> ProvisionFirstCompanyAsync(
        string sessionToken,
        FirstCompanyProvisioningRequest payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return new FirstCompanyProvisioningResponse
            {
                Succeeded = false,
                FailureMessage = "SysAdmin session token is required.",
                FailureCode = "missing_session"
            };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/setup/first-company");
            request.Headers.Add(SysAdminAuthConstants.SessionHeaderName, sessionToken.Trim());
            request.Content = JsonContent.Create(payload);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<FirstCompanyProvisioningResponse>(cancellationToken) ??
                       new FirstCompanyProvisioningResponse
                       {
                           Succeeded = false,
                           FailureCode = "empty_response",
                           FailureMessage = "First-company provisioning returned an empty response."
                       };
            }

            return new FirstCompanyProvisioningResponse
            {
                Succeeded = false,
                FailureCode = $"http_{(int)response.StatusCode}",
                FailureMessage = await ReadMessageAsync(response, cancellationToken)
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to provision the first business company.");
            return new FirstCompanyProvisioningResponse
            {
                Succeeded = false,
                FailureCode = "request_failed",
                FailureMessage = "Unable to provision the first business company."
            };
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

    private sealed class FirstCompanyDecisionRequest
    {
        public bool CreateCompanyNow { get; set; }
    }

    public sealed class SignInResponse
    {
        public string SessionToken { get; set; } = string.Empty;

        public SysAdminAuthSessionSummary Session { get; set; } = new();
    }

    public sealed class SetupStatusResponse
    {
        public string SetupStage { get; set; } = "uninitialized";

        public int AccountCount { get; set; }

        public int CompanyCount { get; set; }

        public int OwnerMembershipCount { get; set; }

        public bool HasAnyAccount { get; set; }

        public bool HasAnyCompany { get; set; }

        public bool HasAnyOwnerMembership { get; set; }

        public bool SetupRequired { get; set; }

        public bool BusinessInitializationPending { get; set; }

        public bool BusinessReady { get; set; }

        public bool FirstCompanySetupRequired { get; set; }

        public bool FirstCompanySetupDeferred { get; set; }

        public DateTimeOffset? FirstCompanySetupDeferredAtUtc { get; set; }

        public bool BootstrapSeedingEnabled { get; set; }

        public bool BootstrapSeedingActive { get; set; }

        public string BootstrapEmailHint { get; set; } = string.Empty;
    }

    public sealed class FirstCompanyProvisioningRequest
    {
        public string OwnerDisplayName { get; set; } = string.Empty;

        public string OwnerEmail { get; set; } = string.Empty;

        public string OwnerPassword { get; set; } = string.Empty;

        public string CompanyName { get; set; } = string.Empty;

        public string EntityType { get; set; } = string.Empty;

        public string Industry { get; set; } = string.Empty;

        public DateTime? IncorporatedOn { get; set; }

        public string FiscalYearEnd { get; set; } = string.Empty;

        public string BusinessNumber { get; set; } = string.Empty;

        public int AccountCodeLength { get; set; }

        public string Phone { get; set; } = string.Empty;

        public string CompanyEmail { get; set; } = string.Empty;

        public string AddressLine { get; set; } = string.Empty;

        public string City { get; set; } = string.Empty;

        public string ProvinceState { get; set; } = string.Empty;

        public string PostalCode { get; set; } = string.Empty;

        public string Country { get; set; } = string.Empty;

        public string TemplateKey { get; set; } = string.Empty;

        public string BaseCurrencyCode { get; set; } = string.Empty;
    }

    public sealed class FirstCompanyProvisioningResponse
    {
        public bool Succeeded { get; set; }

        public string FailureCode { get; set; } = string.Empty;

        public string FailureMessage { get; set; } = string.Empty;

        public CompanyId CompanyId { get; set; }

        public string CompanyEntityNumber { get; set; } = string.Empty;

        public string CompanyName { get; set; } = string.Empty;

        public UserId OwnerUserId { get; set; }

        public string OwnerEmail { get; set; } = string.Empty;

        public Guid? CompanyBookId { get; set; }

        public string CompanyBookCode { get; set; } = string.Empty;

        public string TemplateKey { get; set; } = string.Empty;

        public string TemplateVersion { get; set; } = string.Empty;

        public string BaseCurrencyCode { get; set; } = string.Empty;

        public int AccountCodeLength { get; set; }

        public IReadOnlyList<string> StarterAccountCodes { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> ReservedFamilies { get; set; } = Array.Empty<string>();

        public DateTimeOffset ProvisionedAtUtc { get; set; }
    }

    public sealed record CommandOutcome(bool Succeeded, string Message);
}
