using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for <c>POST /accounting/companies</c> — the Business
/// shell's "+ New Company" flow. Mirrors the failure-shape conventions
/// used by <c>CustomerClient</c> (success + parsed body, or false +
/// user-displayable message).
/// </summary>
public sealed class CompanyProvisioningClient(HttpClient httpClient, ILogger<CompanyProvisioningClient> logger)
{
    public async Task<CreateCompanyOutcome> CreateAsync(
        CreateCompanyPayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "accounting/companies")
            {
                Content = JsonContent.Create(payload),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new CreateCompanyOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var body = await response.Content.ReadFromJsonAsync<CreateCompanyResponse>(cancellationToken);
            return new CreateCompanyOutcome(true, body, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to create the new company.");
            return new CreateCompanyOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record CreateCompanyPayload(
    string CompanyName,
    string EntityType,
    string Industry,
    DateTime IncorporatedOn,
    string FiscalYearEnd,
    string Country,
    string BaseCurrencyCode,
    int? AccountCodeLength = null,
    string? BusinessNumber = null,
    string? Phone = null,
    string? CompanyEmail = null,
    string? AddressLine = null,
    string? City = null,
    string? ProvinceState = null,
    string? PostalCode = null,
    string? TemplateKey = null);

public sealed record CreateCompanyResponse(
    string CompanyId,
    string EntityNumber,
    string CompanyName,
    string BaseCurrencyCode,
    int AccountCodeLength,
    string TemplateKey,
    string TemplateVersion,
    int StarterAccountCount,
    DateTimeOffset ProvisionedAtUtc);

public sealed record CreateCompanyOutcome(
    bool Succeeded,
    CreateCompanyResponse? Saved,
    string? ErrorMessage);
