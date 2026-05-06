using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Read-side wrapper for credit memos. The write side runs through
/// <see cref="BusinessWriteFlowClient.PostCreditMemoAsync"/>; this
/// client only reads. Backed by the credit_notes table — frontend
/// label is "credit memo" but the GL artifact is the same row.
/// Detail uses the existing /credit-notes/{id} endpoint; list uses
/// the new /credit-memos endpoint added with the V1 list rollout.
/// </summary>
public sealed class CreditMemoClient(HttpClient httpClient, ILogger<CreditMemoClient> logger)
{
    public async Task<IReadOnlyList<CreditMemoSummaryDto>> ListAsync(
        CompanyId companyId,
        bool includeDrafts = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/credit-memos?companyId={companyId:D}&includeDrafts={(includeDrafts ? "true" : "false")}";
            var rows = await httpClient.GetFromJsonAsync<CreditMemoSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<CreditMemoSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list credit memos.");
            return Array.Empty<CreditMemoSummaryDto>();
        }
    }

    public async Task<CreditMemoRecordDto?> GetByIdAsync(
        Guid documentId,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"accounting/credit-notes/{documentId:D}?CompanyId={companyId:D}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CreditMemoRecordDto>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load credit memo {DocumentId}.", documentId);
            return null;
        }
    }
}

public sealed record CreditMemoSummaryDto(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DocumentDate,
    Guid CustomerId,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    DateTimeOffset? PostedAt,
    string? CustomerPoNumber);

public sealed record CreditMemoRecordDto(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DocumentDate,
    DateOnly DueDate,
    Guid CustomerId,
    Guid ReceivableAccountId,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal SubtotalAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    string? CustomerPoNumber,
    IReadOnlyList<CreditMemoLineDto> Lines);

public sealed record CreditMemoLineDto(
    int LineNumber,
    Guid RevenueAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineAmount,
    decimal TaxAmount,
    Guid? PayableTaxAccountId);
