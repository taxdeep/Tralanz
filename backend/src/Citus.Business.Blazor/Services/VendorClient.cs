using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company vendor master data. AP-side mirror
/// of <see cref="CustomerClient"/>; mirrors the
/// <c>/accounting/vendors</c> surface on the Accounting API.
/// </summary>
public sealed class VendorClient(HttpClient httpClient, ILogger<VendorClient> logger)
{
    public async Task<IReadOnlyList<VendorSummary>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = includeInactive ? "accounting/vendors?includeInactive=true" : "accounting/vendors";
            var rows = await httpClient.GetFromJsonAsync<VendorSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<VendorSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read vendors.");
            return Array.Empty<VendorSummary>();
        }
    }

    public async Task<VendorSummary?> GetByIdAsync(
        Guid vendorId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<VendorSummary>(
                $"accounting/vendors/{vendorId:D}",
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read vendor {VendorId}.", vendorId);
            return null;
        }
    }

    public Task<VendorMutationOutcome> CreateAsync(
        VendorUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Post, "accounting/vendors", payload, cancellationToken);

    public Task<VendorMutationOutcome> UpdateAsync(
        Guid id,
        VendorUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => SendUpsertAsync(HttpMethod.Put, $"accounting/vendors/{id:D}", payload, cancellationToken);

    private async Task<VendorMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        VendorUpsertPayload payload,
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
                return new VendorMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<VendorSummary>(cancellationToken);
            return new VendorMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert vendor.");
            return new VendorMutationOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record VendorSummary(
    Guid Id,
    CompanyId CompanyId,
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
    Guid? PaymentTermId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record VendorUpsertPayload(
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

public sealed record VendorMutationOutcome(
    bool Succeeded,
    VendorSummary? Saved,
    string? ErrorMessage);
