using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company tax-code catalog. Mirrors the
/// <c>/accounting/tax-codes</c> surface served by the Accounting API.
/// Failures degrade gracefully — list returns empty, mutations return
/// an outcome with the user-displayable message.
/// </summary>
public sealed class TaxCodeClient(HttpClient httpClient, ILogger<TaxCodeClient> logger)
{
    /// <summary>
    /// Fetches the tax codes for the active company. <paramref name="appliesTo"/>
    /// can be "sales", "purchase", "both", or null. Filtering is server-side;
    /// "sales" returns codes whose applies_to is sales OR both.
    /// </summary>
    public async Task<IReadOnlyList<TaxCodeSummary>> ListAsync(
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
            var url = query.Count == 0 ? "accounting/tax-codes" : $"accounting/tax-codes?{string.Join('&', query)}";

            var rows = await httpClient.GetFromJsonAsync<TaxCodeSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<TaxCodeSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read tax codes (appliesTo={AppliesTo}).", appliesTo ?? "<all>");
            return Array.Empty<TaxCodeSummary>();
        }
    }

    public async Task<TaxCodeMutationOutcome> CreateAsync(
        TaxCodeUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendUpsertAsync(HttpMethod.Post, "accounting/tax-codes", payload, cancellationToken);

    public async Task<TaxCodeMutationOutcome> UpdateAsync(
        Guid id,
        TaxCodeUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendUpsertAsync(HttpMethod.Put, $"accounting/tax-codes/{id:D}", payload, cancellationToken);

    public async Task<TaxCodeMutationOutcome> SetActiveAsync(
        Guid id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var path = isActive ? $"accounting/tax-codes/{id:D}/activate" : $"accounting/tax-codes/{id:D}/deactivate";
        try
        {
            using var response = await httpClient.PostAsync(path, content: null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadMessageAsync(response, cancellationToken);
                return new TaxCodeMutationOutcome(false, null, error);
            }
            var saved = await response.Content.ReadFromJsonAsync<TaxCodeSummary>(cancellationToken);
            return new TaxCodeMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to toggle tax code active flag.");
            return new TaxCodeMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<TaxCodeMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        TaxCodeUpsertPayload payload,
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
                var error = await ReadMessageAsync(response, cancellationToken);
                return new TaxCodeMutationOutcome(false, null, error);
            }
            var saved = await response.Content.ReadFromJsonAsync<TaxCodeSummary>(cancellationToken);
            return new TaxCodeMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert tax code.");
            return new TaxCodeMutationOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record TaxCodeSummary(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string Code,
    string Name,
    decimal RatePercent,
    string AppliesTo,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TaxCodeUpsertPayload(
    string Code,
    string Name,
    decimal RatePercent,
    string AppliesTo,
    bool IsActive);

public sealed record TaxCodeMutationOutcome(bool Succeeded, TaxCodeSummary? Saved, string? ErrorMessage);
