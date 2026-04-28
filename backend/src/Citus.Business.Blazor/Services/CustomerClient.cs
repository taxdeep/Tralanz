using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company customer master data. Mirrors the
/// <c>/accounting/customers</c> surface served by the Accounting API.
/// Failures degrade gracefully: list returns empty, mutations carry a
/// user-displayable message in the outcome.
/// </summary>
public sealed class CustomerClient(HttpClient httpClient, ILogger<CustomerClient> logger)
{
    public async Task<IReadOnlyList<CustomerSummary>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = includeInactive ? "accounting/customers?includeInactive=true" : "accounting/customers";
            var rows = await httpClient.GetFromJsonAsync<CustomerSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<CustomerSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read customers.");
            return Array.Empty<CustomerSummary>();
        }
    }

    public async Task<CustomerMutationOutcome> CreateAsync(
        CustomerUpsertPayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync("accounting/customers", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new CustomerMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<CustomerSummary>(cancellationToken);
            return new CustomerMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to create customer.");
            return new CustomerMutationOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record CustomerSummary(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string DisplayName,
    string DefaultCurrencyCode,
    string? Email,
    string? Phone,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    string? TaxId,
    string? Notes,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CustomerUpsertPayload(
    string DisplayName,
    string DefaultCurrencyCode,
    string? Email,
    string? Phone,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    string? TaxId,
    string? Notes);

public sealed record CustomerMutationOutcome(
    bool Succeeded,
    CustomerSummary? Saved,
    string? ErrorMessage);
