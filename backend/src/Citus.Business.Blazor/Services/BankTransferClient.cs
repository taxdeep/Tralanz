using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class BankTransferClient(HttpClient httpClient, ILogger<BankTransferClient> logger)
{
    public async Task<IReadOnlyList<BankTransferSummaryDto>> ListAsync(
        CompanyId companyId,
        bool includeDrafts = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/bank-transfers?companyId={companyId:D}&includeDrafts={(includeDrafts ? "true" : "false")}";
            var rows = await httpClient.GetFromJsonAsync<BankTransferSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<BankTransferSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list bank transfers.");
            return Array.Empty<BankTransferSummaryDto>();
        }
    }

    public async Task<BankTransferRecordDto?> GetByIdAsync(
        Guid documentId,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"accounting/bank-transfers/{documentId:D}?CompanyId={companyId:D}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<BankTransferRecordDto>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load bank transfer {DocumentId}.", documentId);
            return null;
        }
    }
}

public sealed record BankTransferSummaryDto(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly TransferDate,
    Guid FromAccountId,
    string FromCurrencyCode,
    Guid ToAccountId,
    string ToCurrencyCode,
    decimal Amount,
    decimal? FxRate,
    DateTimeOffset? PostedAt);

public sealed record BankTransferRecordDto(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly TransferDate,
    Guid FromAccountId,
    string FromCurrencyCode,
    Guid ToAccountId,
    string ToCurrencyCode,
    decimal Amount,
    decimal? FxRate,
    string? ReferenceNo,
    string? Memo);
