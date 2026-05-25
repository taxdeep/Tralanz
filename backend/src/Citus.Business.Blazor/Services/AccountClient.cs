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
            var url = query.Count == 0 ? "accounting/accounts" : $"accounting/accounts?{string.Join('&', query)}";

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
        => await SendUpsertAsync(HttpMethod.Post, "accounting/accounts", payload, cancellationToken);

    public async Task<AccountMutationOutcome> UpdateAsync(
        Guid id,
        AccountUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendUpsertAsync(HttpMethod.Put, $"accounting/accounts/{id:D}", payload, cancellationToken);

    /// <summary>
    /// Lists available chart-of-accounts starter templates. Failures
    /// degrade to an empty list so the maintenance UI can still render.
    /// </summary>
    public async Task<IReadOnlyList<CoaTemplateSummary>> ListTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await httpClient.GetFromJsonAsync<CoaTemplateSummary[]>(
                "accounting/accounts/templates",
                cancellationToken);
            return rows ?? Array.Empty<CoaTemplateSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read CoA templates.");
            return Array.Empty<CoaTemplateSummary>();
        }
    }

    /// <summary>
    /// Applies a starter template to the active company. The seeder is
    /// additive: rows whose <c>code</c> already exists are skipped, so
    /// the call is safe to retry.
    /// </summary>
    public async Task<CoaSeedOutcome> ApplyTemplateAsync(
        string templateKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/accounts/templates/{Uri.EscapeDataString(templateKey)}/apply",
                content: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new CoaSeedOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var summary = await response.Content.ReadFromJsonAsync<CoaSeedSummaryDto>(cancellationToken);
            return new CoaSeedOutcome(true, summary, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to apply CoA template.");
            return new CoaSeedOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    public async Task<AccountMutationOutcome> SetActiveAsync(
        Guid id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var path = isActive ? $"accounting/accounts/{id:D}/activate" : $"accounting/accounts/{id:D}/deactivate";
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

    /// <summary>
    /// Batch D: lock or unlock an account. While locked, the server
    /// refuses to modify financial-truth fields (code/name/root_type/
    /// detail_type/currency/allow_manual_posting). Re-parenting and
    /// activate/deactivate still work even when locked.
    /// </summary>
    public async Task<AccountMutationOutcome> SetLockAsync(
        Guid id,
        bool @lock,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/accounts/{id:D}/lock",
                new AccountLockPayload(@lock),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AccountMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<AccountSummary>(cancellationToken);
            return new AccountMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to toggle account lock flag.");
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
    CompanyId CompanyId,
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
    DateTimeOffset UpdatedAt,
    // Batch C: null = top-level account.
    Guid? ParentAccountId = null,
    // Batch D: when non-null, account is locked.
    DateTimeOffset? LockedAt = null,
    string? LockedByUserId = null);

public sealed record AccountUpsertPayload(
    string Code,
    string Name,
    string RootType,
    string? DetailType,
    string? CurrencyCode,
    bool AllowManualPosting,
    bool IsActive,
    // Batch C
    Guid? ParentAccountId = null);

/// <summary>Batch D: lock-toggle payload.</summary>
public sealed record AccountLockPayload(bool Lock);

public sealed record AccountMutationOutcome(bool Succeeded, AccountSummary? Saved, string? ErrorMessage);

public sealed record CoaTemplateSummary(
    string Key,
    string Version,
    string Name,
    string Description,
    string Country,
    int AccountCodeLength,
    int AccountCount,
    IReadOnlyList<CoaTemplateAccountSummary> Accounts);

public sealed record CoaTemplateAccountSummary(
    string Code,
    string Name,
    string RootType,
    string? DetailType,
    bool AllowManualPosting,
    string? SystemKey,
    string? SystemRole);

public sealed record CoaSeedSummaryDto(
    string TemplateKey,
    string TemplateVersion,
    int CreatedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<CoaSeedAccountResultDto> Results);

public sealed record CoaSeedAccountResultDto(
    string Code,
    string Name,
    string Outcome,        // "Created" | "SkippedExisting" | "Failed"
    string? ErrorMessage);

public sealed record CoaSeedOutcome(bool Succeeded, CoaSeedSummaryDto? Summary, string? ErrorMessage);
