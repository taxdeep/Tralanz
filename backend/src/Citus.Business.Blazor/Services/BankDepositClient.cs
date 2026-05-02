using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class BankDepositClient(HttpClient httpClient, ILogger<BankDepositClient> logger)
{
    public async Task<IReadOnlyList<BankDepositSummaryDto>> ListAsync(
        Guid companyId,
        bool includeDrafts = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/bank-deposits?companyId={companyId:D}&includeDrafts={(includeDrafts ? "true" : "false")}";
            var rows = await httpClient.GetFromJsonAsync<BankDepositSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<BankDepositSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list bank deposits.");
            return Array.Empty<BankDepositSummaryDto>();
        }
    }

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

public sealed record BankDepositSummaryDto(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DepositDate,
    Guid DepositToAccountId,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    int ItemCount,
    DateTimeOffset? PostedAt);

public sealed record BankDepositItemDto(
    int LineNumber,
    string SourceItemKind,
    Guid? SourceItemId,
    string SourceItemDisplayNumber,
    string? PayerName,
    string? PaymentMethod,
    string? ReferenceNo,
    decimal Amount);
