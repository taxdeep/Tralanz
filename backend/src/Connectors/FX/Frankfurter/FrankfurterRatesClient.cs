using System.Net.Http.Json;
using System.Text.Json;
using Engines.FX.FxRateLookup;
using SharedKernel.FX;

namespace Connectors.FX.Frankfurter;

public sealed class FrankfurterRatesClient : IFxProviderClient
{
    private readonly HttpClient _httpClient;

    public FrankfurterRatesClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<IReadOnlyList<FxMarketRateRecord>> GetRatesAsync(
        string providerKey,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        var requestUri =
            $"/v2/rates?base={Uri.EscapeDataString(baseCurrencyCode)}" +
            $"&quotes={Uri.EscapeDataString(quoteCurrencyCode)}" +
            $"&from={fromDate:yyyy-MM-dd}" +
            $"&to={toDate:yyyy-MM-dd}" +
            $"&providers={Uri.EscapeDataString(providerKey)}";

        var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var rateRows = await response.Content.ReadFromJsonAsync<List<FrankfurterRateRow>>(cancellationToken: cancellationToken)
            ?? [];

        return rateRows
            .Where(row => row.Rate > 0m)
            .Select(row => new FxMarketRateRecord(
                Guid.Empty,
                providerKey,
                row.Base.ToUpperInvariant(),
                row.Quote.ToUpperInvariant(),
                row.Date,
                row.Rate,
                FxRateType.Spot,
                FxQuoteBasis.Direct,
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(row)))
            .ToArray();
    }
}
