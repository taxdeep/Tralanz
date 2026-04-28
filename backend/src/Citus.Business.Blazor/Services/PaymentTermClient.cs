using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company payment-term catalog. Mirrors the
/// <c>/accounting/payment-terms</c> surface on the Accounting API.
/// Failures degrade gracefully — list returns empty, mutations return
/// an outcome with the user-displayable message.
/// </summary>
public sealed class PaymentTermClient(HttpClient httpClient, ILogger<PaymentTermClient> logger)
{
    public async Task<IReadOnlyList<PaymentTermSummary>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = includeInactive ? "accounting/payment-terms?includeInactive=true" : "accounting/payment-terms";
            var rows = await httpClient.GetFromJsonAsync<PaymentTermSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<PaymentTermSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read payment terms.");
            return Array.Empty<PaymentTermSummary>();
        }
    }

    public Task<PaymentTermMutationOutcome> CreateAsync(
        PaymentTermUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Post, "accounting/payment-terms", payload, cancellationToken);

    public Task<PaymentTermMutationOutcome> UpdateAsync(
        Guid id,
        PaymentTermUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Put, $"accounting/payment-terms/{id:D}", payload, cancellationToken);

    public async Task<PaymentTermMutationOutcome> SetActiveAsync(
        Guid id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var path = isActive ? $"accounting/payment-terms/{id:D}/activate" : $"accounting/payment-terms/{id:D}/deactivate";
        try
        {
            using var response = await httpClient.PostAsync(path, content: null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadMessageAsync(response, cancellationToken);
                return new PaymentTermMutationOutcome(false, null, error);
            }
            var saved = await response.Content.ReadFromJsonAsync<PaymentTermSummary>(cancellationToken);
            return new PaymentTermMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to toggle payment term active flag.");
            return new PaymentTermMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<PaymentTermMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        PaymentTermUpsertPayload payload,
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
                return new PaymentTermMutationOutcome(false, null, error);
            }
            var saved = await response.Content.ReadFromJsonAsync<PaymentTermSummary>(cancellationToken);
            return new PaymentTermMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert payment term.");
            return new PaymentTermMutationOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record PaymentTermSummary(
    Guid Id,
    Guid CompanyId,
    string Code,
    string Name,
    int NetDays,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PaymentTermUpsertPayload(
    string Code,
    string Name,
    int NetDays,
    bool IsActive);

public sealed record PaymentTermMutationOutcome(bool Succeeded, PaymentTermSummary? Saved, string? ErrorMessage);
