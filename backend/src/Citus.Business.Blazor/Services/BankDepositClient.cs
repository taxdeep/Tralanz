using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class BankDepositClient(HttpClient httpClient, ILogger<BankDepositClient> logger)
{
    public async Task<BankDepositRecordDto?> GetByIdAsync(
        Guid documentId,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"accounting/bank-deposits/{documentId:D}?CompanyId={companyId:D}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<BankDepositRecordDto>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load bank deposit {DocumentId}.", documentId);
            return null;
        }
    }
}

public sealed record BankDepositRecordDto(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DepositDate,
    Guid DepositToAccountId,
    Guid UndepositedFundsAccountId,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    string? ReferenceNo,
    string? Memo,
    IReadOnlyList<BankDepositItemDto> Items);

public sealed record BankDepositItemDto(
    int LineNumber,
    string SourceItemKind,
    Guid? SourceItemId,
    string SourceItemDisplayNumber,
    string? PayerName,
    string? PaymentMethod,
    string? ReferenceNo,
    decimal Amount);
