using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class BankTransferClient(HttpClient httpClient, ILogger<BankTransferClient> logger)
{
    public async Task<BankTransferRecordDto?> GetByIdAsync(
        Guid documentId,
        Guid companyId,
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

public sealed record BankTransferRecordDto(
    Guid Id,
    Guid CompanyId,
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
