using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company chart of accounts. Mirrors the
/// <c>/accounting/accounts</c> surface served by the Accounting API.
/// Failures degrade gracefully — list returns empty, mutations carry
/// a user-displayable message in the outcome.
/// </summary>
public sealed class AccountClient(HttpClient httpClient, ILogger<AccountClient> logger)
{
    public async Task<IReadOnlyList<AccountSummary>> ListAsync(
        string? rootType = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(rootType))
            {
                query.Add($"rootType={Uri.EscapeDataString(rootType)}");
            }
            if (includeInactive)
            {
                query.Add("includeInactive=true");
            }
            var url = query.Count == 0 ? "accounts" : $"accounts?{string.Join('&', query)}";

            var rows = await httpClient.GetFromJsonAsync<AccountSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<AccountSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read accounts (rootType={RootType}).", rootType ?? "<all>");
            return Array.Empty<AccountSummary>();
        }
    }

    public async Task<AccountMutationOutcome> CreateAsync(
        AccountUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendUpsertAsync(HttpMethod.Post, "accounts", payload, cancellationToken);

    public async Task<AccountMutationOutcome> UpdateAsync(
        Guid id,
        AccountUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendUpsertAsync(HttpMethod.Put, $"accounts/{id:D}", payload, cancellationToken);

    public async Task<AccountMutationOutcome> SetActiveAsync(
        Guid id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var path = isActive ? $"accounts/{id:D}/activate" : $"accounts/{id:D}/deactivate";
        try
        {
            using var response = await httpClient.PostAsync(path, content: null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AccountMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<AccountSummary>(cancellationToken);
            return new AccountMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to toggle account active flag.");
            return new AccountMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<AccountMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        AccountUpsertPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(payload),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AccountMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<AccountSummary>(cancellationToken);
            return new AccountMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert account.");
            return new AccountMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return $"Request failed with status code {(int)response.StatusCode}.";
        }
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? raw;
            }
        }
        catch (JsonException) { }
        return raw;
    }
}

public sealed record AccountSummary(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string Code,
    string Name,
    string RootType,
    string? DetailType,
    bool IsActive,
    bool IsSystem,
    bool IsSystemDefault,
    string? SystemKey,
    string? SystemRole,
    string? CurrencyCode,
    bool AllowManualPosting,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AccountUpsertPayload(
    string Code,
    string Name,
    string RootType,
    string? DetailType,
    string? CurrencyCode,
    bool AllowManualPosting,
    bool IsActive);

public sealed record AccountMutationOutcome(bool Succeeded, AccountSummary? Saved, string? ErrorMessage);
