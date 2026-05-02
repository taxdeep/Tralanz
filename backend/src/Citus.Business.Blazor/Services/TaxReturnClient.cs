using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class TaxReturnClient(HttpClient httpClient, ILogger<TaxReturnClient> logger)
{
    public async Task<IReadOnlyList<TaxReturnSummaryDto>> ListAsync(
        Guid companyId,
        bool includeDrafts = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/tax-returns?companyId={companyId:D}&includeDrafts={(includeDrafts ? "true" : "false")}";
            var rows = await httpClient.GetFromJsonAsync<TaxReturnSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<TaxReturnSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list tax returns.");
            return Array.Empty<TaxReturnSummaryDto>();
        }
    }

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

public sealed record TaxReturnSummaryDto(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    string TaxRegime,
    string FilingFrequency,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal NetAmount,
    string BaseCurrencyCode,
    DateTimeOffset? PostedAt);

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
