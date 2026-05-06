using System.Net.Http.Json;
using Citus.SysAdmin.Blazor.State;
using Citus.Ui.Shared.Control;

namespace Citus.SysAdmin.Blazor.Services;

public sealed class AiProviderConfigClient(
    HttpClient httpClient,
    AppShellState shellState,
    ILogger<AiProviderConfigClient> logger)
{
    public async Task<AiProviderConfigDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<AiProviderConfigDto>(
                "control/operations/ai-provider",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load AI provider config.");
            return null;
        }
    }

    public async Task<AiProviderConfigOutcome> SaveAsync(
        AiProviderConfigUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PutAsJsonAsync(
                "control/operations/ai-provider",
                request,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return new AiProviderConfigOutcome(false, error, null);
            }

            var saved = await response.Content.ReadFromJsonAsync<AiProviderConfigDto>(cancellationToken);
            return new AiProviderConfigOutcome(true, null, saved);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save AI provider config.");
            return new AiProviderConfigOutcome(false, ex.Message, null);
        }
    }

    public async Task<AiProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PostAsync(
                "control/operations/ai-provider/test",
                content: null,
                cancellationToken);

            var body = await response.Content.ReadFromJsonAsync<AiProviderTestBody>(cancellationToken);
            return new AiProviderTestOutcome(
                Succeeded: body?.Succeeded ?? false,
                HttpStatus: body?.HttpStatus,
                Message: body?.Message ?? $"Server returned {(int)response.StatusCode}.",
                ElapsedMs: body?.ElapsedMs);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI provider test failed.");
            return new AiProviderTestOutcome(false, null, ex.Message, null);
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

    private sealed record AiProviderTestBody(
        bool Succeeded,
        int? HttpStatus,
        string Message,
        double? ElapsedMs);
}

public sealed record AiProviderConfigDto(
    string Provider,
    string? BaseUrl,
    string Model,
    int MaxTokens,
    double Temperature,
    bool HasApiKey,
    DateTimeOffset UpdatedAt,
    UserId? UpdatedByUserId);

public sealed record AiProviderConfigUpsertDto(
    string Provider,
    string? BaseUrl,
    string Model,
    int MaxTokens,
    double Temperature,
    string? NewApiKey,
    bool ClearApiKey);

public sealed record AiProviderConfigOutcome(
    bool Succeeded,
    string? ErrorMessage,
    AiProviderConfigDto? Saved);

public sealed record AiProviderTestOutcome(
    bool Succeeded,
    int? HttpStatus,
    string Message,
    double? ElapsedMs);
