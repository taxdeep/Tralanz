using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the persisted shipping address book on the Customer
/// detail page. Distinct from the historical-address picker exposed by
/// <see cref="CustomerClient.ListShippingAddressHistoryAsync"/>:
///   * History — read-only, derived from past quote / SO documents.
///   * Book    — CRUD, lives in customer_shipping_address_book.
///
/// Both surfaces fail soft. Mutation calls return a typed outcome so
/// the page can show a validation message inline instead of throwing.
/// </summary>
public sealed class CustomerShippingAddressBookClient(
    HttpClient httpClient,
    ILogger<CustomerShippingAddressBookClient> logger)
{
    public async Task<IReadOnlyList<CustomerShippingAddressBookDto>> ListAsync(
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await httpClient.GetFromJsonAsync<CustomerShippingAddressBookDto[]>(
                $"accounting/customers/{customerId:D}/shipping-address-book",
                cancellationToken);
            return rows ?? Array.Empty<CustomerShippingAddressBookDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list shipping address book for {CustomerId}.", customerId);
            return Array.Empty<CustomerShippingAddressBookDto>();
        }
    }

    public async Task<CustomerShippingAddressBookMutationOutcome> InsertAsync(
        Guid customerId,
        CustomerShippingAddressBookPayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/customers/{customerId:D}/shipping-address-book",
                payload,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<CustomerShippingAddressBookDto>(cancellationToken);
            return new(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Insert shipping address book entry failed for {CustomerId}.", customerId);
            return new(false, null, "Could not reach the server. Please try again.");
        }
    }

    public async Task<CustomerShippingAddressBookMutationOutcome> UpdateAsync(
        Guid customerId,
        Guid addressId,
        CustomerShippingAddressBookPayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PutAsJsonAsync(
                $"accounting/customers/{customerId:D}/shipping-address-book/{addressId:D}",
                payload,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<CustomerShippingAddressBookDto>(cancellationToken);
            return new(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update shipping address book entry failed for {CustomerId}/{AddressId}.", customerId, addressId);
            return new(false, null, "Could not reach the server. Please try again.");
        }
    }

    public async Task<bool> DeleteAsync(
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.DeleteAsync(
                $"accounting/customers/{customerId:D}/shipping-address-book/{addressId:D}",
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delete shipping address book entry failed for {CustomerId}/{AddressId}.", customerId, addressId);
            return false;
        }
    }

    public async Task<CustomerShippingAddressBookMutationOutcome> SetDefaultAsync(
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/customers/{customerId:D}/shipping-address-book/{addressId:D}/set-default",
                content: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<CustomerShippingAddressBookDto>(cancellationToken);
            return new(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Set-default shipping address book entry failed for {CustomerId}/{AddressId}.", customerId, addressId);
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

public sealed record CustomerShippingAddressBookDto(
    Guid Id,
    Guid CompanyId,
    Guid CustomerId,
    string? Label,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CustomerShippingAddressBookPayload(
    string? Label,
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    bool IsDefault);

public sealed record CustomerShippingAddressBookMutationOutcome(
    bool Succeeded,
    CustomerShippingAddressBookDto? Saved,
    string? ErrorMessage);
