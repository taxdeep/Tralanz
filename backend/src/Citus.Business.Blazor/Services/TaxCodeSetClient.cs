using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company Tax Code bundles (<c>tax_code_sets</c>) —
/// the R2 "Tax Code" layer that groups Tax Rules. Mirrors
/// <see cref="TaxCodeClient"/> against <c>/accounting/tax-code-sets</c>:
/// list (for pickers) + create / edit / activate (for the Tax Code editor).
/// </summary>
public sealed class TaxCodeSetClient(HttpClient httpClient, ILogger<TaxCodeSetClient> logger)
{
    public async Task<IReadOnlyList<TaxCodeSetSummary>> ListAsync(
        string? appliesTo = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(appliesTo))
            {
                query.Add($"appliesTo={Uri.EscapeDataString(appliesTo)}");
            }
            if (includeInactive)
            {
                query.Add("includeInactive=true");
            }
            var url = query.Count == 0
                ? "accounting/tax-code-sets"
                : $"accounting/tax-code-sets?{string.Join('&', query)}";

            var rows = await httpClient.GetFromJsonAsync<TaxCodeSetSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<TaxCodeSetSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read tax code sets (appliesTo={AppliesTo}).", appliesTo ?? "<all>");
            return Array.Empty<TaxCodeSetSummary>();
        }
    }

    public async Task<TaxCodeSetMutationOutcome> CreateAsync(
        TaxCodeSetUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendUpsertAsync(HttpMethod.Post, "accounting/tax-code-sets", payload, cancellationToken);

    public async Task<TaxCodeSetMutationOutcome> UpdateAsync(
        Guid id,
        TaxCodeSetUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendUpsertAsync(HttpMethod.Put, $"accounting/tax-code-sets/{id:D}", payload, cancellationToken);

    public async Task<TaxCodeSetMutationOutcome> SetActiveAsync(
        Guid id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var path = isActive
            ? $"accounting/tax-code-sets/{id:D}/activate"
            : $"accounting/tax-code-sets/{id:D}/deactivate";
        try
        {
            using var response = await httpClient.PostAsync(path, content: null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new TaxCodeSetMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<TaxCodeSetSummary>(cancellationToken);
            return new TaxCodeSetMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to toggle tax code active flag.");
            return new TaxCodeSetMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<TaxCodeSetMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        TaxCodeSetUpsertPayload payload,
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
                return new TaxCodeSetMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<TaxCodeSetSummary>(cancellationToken);
            return new TaxCodeSetMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert tax code set.");
            return new TaxCodeSetMutationOutcome(false, null, "Unable to reach the server. Please try again.");
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
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return message.GetString() ?? raw;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Fall through to the raw payload.
        }
        return raw;
    }
}

public sealed record TaxCodeSetSummary(
    Guid Id,
    string Code,
    string Name,
    string AppliesTo,
    bool IsActive,
    IReadOnlyList<TaxCodeSetMemberSummary> Members);

public sealed record TaxCodeSetMemberSummary(
    Guid RuleId,
    string RuleCode,
    string RuleName,
    decimal RatePercent,
    int Sequence,
    bool IsCompound);

public sealed record TaxCodeSetUpsertPayload(
    string Code,
    string Name,
    string AppliesTo,
    bool IsActive,
    IReadOnlyList<TaxCodeSetMemberPayload> Members);

public sealed record TaxCodeSetMemberPayload(
    Guid RuleId,
    int Sequence,
    bool IsCompound);

public sealed record TaxCodeSetMutationOutcome(bool Succeeded, TaxCodeSetSummary? Saved, string? ErrorMessage);
