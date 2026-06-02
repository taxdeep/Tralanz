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

    public async Task<IReadOnlyList<Components.Shared.CustomerShippingAddressSuggestion>> ListShippingAddressHistoryAsync(
        Guid customerId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/customers/{customerId:D}/shipping-addresses?limit={limit}";
            var rows = await httpClient.GetFromJsonAsync<Components.Shared.CustomerShippingAddressSuggestion[]>(
                url, cancellationToken);
            return rows ?? Array.Empty<Components.Shared.CustomerShippingAddressSuggestion>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read shipping-address history for {CustomerId}.", customerId);
            return Array.Empty<Components.Shared.CustomerShippingAddressSuggestion>();
        }
    }

    public async Task<CustomerSummary?> GetByIdAsync(
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<CustomerSummary>(
                $"accounting/customers/{customerId:D}",
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read customer {CustomerId}.", customerId);
            return null;
        }
    }

    public Task<CustomerMutationOutcome> CreateAsync(
        CustomerUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Post, "accounting/customers", payload, cancellationToken);

    public Task<CustomerMutationOutcome> UpdateAsync(
        Guid id,
        CustomerUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Put, $"accounting/customers/{id:D}", payload, cancellationToken);

    private async Task<CustomerMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        CustomerUpsertPayload payload,
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
                return new CustomerMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<CustomerSummary>(cancellationToken);
            return new CustomerMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert customer.");
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
    CompanyId CompanyId,
    string EntityNumber,
    // Operator-facing customer code (CUSNNNNNN); null on rows created
    // before the customer-display scope was wired in.
    string? CustomerNumber,
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
    Guid? PaymentTermId,
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
    string? Notes,
    Guid? PaymentTermId);

public sealed record CustomerMutationOutcome(
    bool Succeeded,
    CustomerSummary? Saved,
    string? ErrorMessage);
