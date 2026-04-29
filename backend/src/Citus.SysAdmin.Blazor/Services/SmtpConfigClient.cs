using System.Net.Http.Json;
using Citus.SysAdmin.Blazor.State;
using Citus.Ui.Shared.Control;

namespace Citus.SysAdmin.Blazor.Services;

public sealed class SmtpConfigClient(
    HttpClient httpClient,
    AppShellState shellState,
    ILogger<SmtpConfigClient> logger)
{
    public async Task<SmtpConfigDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<SmtpConfigDto>(
                "control/operations/smtp",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load SMTP config.");
            return null;
        }
    }

    public async Task<SmtpConfigOutcome> SaveAsync(
        SmtpConfigUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PutAsJsonAsync(
                "control/operations/smtp",
                request,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return new SmtpConfigOutcome(false, error, null);
            }

            var saved = await response.Content.ReadFromJsonAsync<SmtpConfigDto>(cancellationToken);
            return new SmtpConfigOutcome(true, null, saved);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save SMTP config.");
            return new SmtpConfigOutcome(false, ex.Message, null);
        }
    }

    public async Task<SmtpTestSendOutcome> SendTestAsync(
        string toEmail,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PostAsJsonAsync(
                "control/operations/smtp/test",
                new { toEmail },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return new SmtpTestSendOutcome(false, error);
            }

            var body = await response.Content.ReadFromJsonAsync<SmtpTestSendBody>(cancellationToken);
            return new SmtpTestSendOutcome(body?.Succeeded ?? false, body?.FailureMessage);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP test send failed.");
            return new SmtpTestSendOutcome(false, ex.Message);
        }
    }

    private void ApplySessionHeader()
    {
        httpClient.DefaultRequestHeaders.Remove(SysAdminAuthConstants.SessionHeaderName);
        if (shellState.IsAuthenticated)
        {
            httpClient.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, shellState.SessionToken);
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken);
            return body?.Message ?? $"Server returned {(int)response.StatusCode}.";
        }
        catch
        {
            return $"Server returned {(int)response.StatusCode}.";
        }
    }

    private sealed record ErrorBody(string Message);

    private sealed record SmtpTestSendBody(bool Succeeded, string ProviderKey, string? FailureMessage);
}

public sealed record SmtpConfigDto(
    string Provider,
    string FromEmail,
    string FromDisplayName,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    bool HasPassword,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedByUserId);

public sealed record SmtpConfigUpsertDto(
    string Provider,
    string FromEmail,
    string FromDisplayName,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string? NewPassword,
    bool ClearPassword);

public sealed record SmtpConfigOutcome(bool Succeeded, string? ErrorMessage, SmtpConfigDto? Saved);

public sealed record SmtpTestSendOutcome(bool Succeeded, string? ErrorMessage);
