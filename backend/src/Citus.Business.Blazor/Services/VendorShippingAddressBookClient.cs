using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the persisted shipping address book on the Vendor
/// detail page — mirror of <see cref="CustomerShippingAddressBookClient"/>.
/// </summary>
public sealed class VendorShippingAddressBookClient(
    HttpClient httpClient,
    ILogger<VendorShippingAddressBookClient> logger)
{
    public async Task<IReadOnlyList<VendorShippingAddressBookDto>> ListAsync(
        Guid vendorId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await httpClient.GetFromJsonAsync<VendorShippingAddressBookDto[]>(
                $"accounting/vendors/{vendorId:D}/shipping-address-book",
                cancellationToken);
            return rows ?? Array.Empty<VendorShippingAddressBookDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list shipping address book for vendor {VendorId}.", vendorId);
            return Array.Empty<VendorShippingAddressBookDto>();
        }
    }

    public async Task<VendorShippingAddressBookMutationOutcome> InsertAsync(
        Guid vendorId,
        VendorShippingAddressBookPayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/vendors/{vendorId:D}/shipping-address-book",
                payload,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<VendorShippingAddressBookDto>(cancellationToken);
            return new(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Insert shipping address book entry failed for vendor {VendorId}.", vendorId);
            return new(false, null, "Could not reach the server. Please try again.");
        }
    }

    public async Task<VendorShippingAddressBookMutationOutcome> UpdateAsync(
        Guid vendorId,
        Guid addressId,
        VendorShippingAddressBookPayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PutAsJsonAsync(
                $"accounting/vendors/{vendorId:D}/shipping-address-book/{addressId:D}",
                payload,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<VendorShippingAddressBookDto>(cancellationToken);
            return new(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update shipping address book entry failed for vendor {VendorId}/{AddressId}.", vendorId, addressId);
            return new(false, null, "Could not reach the server. Please try again.");
        }
    }

    public async Task<bool> DeleteAsync(
        Guid vendorId,
        Guid addressId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.DeleteAsync(
                $"accounting/vendors/{vendorId:D}/shipping-address-book/{addressId:D}",
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delete shipping address book entry failed for vendor {VendorId}/{AddressId}.", vendorId, addressId);
            return false;
        }
    }

    public async Task<VendorShippingAddressBookMutationOutcome> SetDefaultAsync(
        Guid vendorId,
        Guid addressId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/vendors/{vendorId:D}/shipping-address-book/{addressId:D}/set-default",
                content: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<VendorShippingAddressBookDto>(cancellationToken);
            return new(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Set-default shipping address book entry failed for vendor {VendorId}/{AddressId}.", vendorId, addressId);
            return new(false, null, "Could not reach the server. Please try again.");
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
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return message.GetString() ?? raw;
            }
        }
        catch (System.Text.Json.JsonException) { }
        return raw;
    }
}

public sealed record VendorShippingAddressBookDto(
    Guid Id,
    Guid CompanyId,
    Guid VendorId,
    string? Label,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record VendorShippingAddressBookPayload(
    string? Label,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    bool IsDefault);

public sealed record VendorShippingAddressBookMutationOutcome(
    bool Succeeded,
    VendorShippingAddressBookDto? Saved,
    string? ErrorMessage);
