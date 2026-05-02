using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class TaxReturnClient(HttpClient httpClient, ILogger<TaxReturnClient> logger)
{
    public async Task<TaxReturnRecordDto?> GetByIdAsync(
        Guid documentId,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"accounting/tax-returns/{documentId:D}?CompanyId={companyId:D}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TaxReturnRecordDto>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load tax return {DocumentId}.", documentId);
            return null;
        }
    }
}

public sealed record TaxReturnRecordDto(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    string TaxRegime,
    string FilingFrequency,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string BaseCurrencyCode,
    decimal CollectedAmount,
    decimal InputCreditsAmount,
    decimal AdjustmentsAmount,
    string? AdjustmentsNote,
    decimal NetAmount,
    string? RegulatorReferenceNo,
    string? Memo);
