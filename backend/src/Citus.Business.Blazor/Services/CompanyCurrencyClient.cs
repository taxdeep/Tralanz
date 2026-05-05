using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company multi-currency surface. Mirrors the
/// <c>/accounting/company/currencies</c> endpoints. The active company is
/// resolved server-side from the X-Citus-Business-Active-Company-Id header
/// attached by <see cref="BusinessSessionHeaderHandler"/>; callers don't pass
/// a company id. Failures degrade gracefully: list returns null (so the UI
/// can render an empty placeholder), mutations carry a user-displayable
/// message in the outcome.
/// </summary>
public sealed class CompanyCurrencyClient(HttpClient httpClient, ILogger<CompanyCurrencyClient> logger)
{
    public async Task<CompanyCurrencyProfileSummary?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<CompanyCurrencyProfileSummary>(
                "accounting/company/currencies",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read company currencies.");
            return null;
        }
    }

    public async Task<CompanyCurrencyMutationOutcome> EnableAsync(
        string currencyCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "accounting/company/currencies",
                new EnableCompanyCurrencyPayload(currencyCode),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new CompanyCurrencyMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<EnableCompanyCurrencyResponse>(cancellationToken);
            return new CompanyCurrencyMutationOutcome(true, saved?.Profile, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to enable currency {CurrencyCode}.", currencyCode);
            return new CompanyCurrencyMutationOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record CompanyCurrencyProfileSummary(
    CompanyId CompanyId,
    string LegalName,
    string BaseCurrencyCode,
    bool MultiCurrencyEnabled,
    IReadOnlyList<CompanyCurrencyEntry> Currencies);

public sealed record CompanyCurrencyEntry(
    string CurrencyCode,
    string CurrencyName,
    bool IsBaseCurrency,
    bool IsEnabled);

public sealed record EnableCompanyCurrencyPayload(string CurrencyCode);

public sealed record EnableCompanyCurrencyResponse(
    CompanyCurrencyProfileSummary Profile,
    IReadOnlyList<ProvisionedControlAccountSummary> ProvisionedControlAccounts);

public sealed record ProvisionedControlAccountSummary(
    Guid AccountId,
    string Code,
    string Name,
    string CurrencyCode,
    string SystemRole,
    bool WasCreated);

public sealed record CompanyCurrencyMutationOutcome(
    bool Succeeded,
    CompanyCurrencyProfileSummary? Profile,
    string? ErrorMessage);
