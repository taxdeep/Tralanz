using System.Net;
using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Read-side wrapper for vendor credits. The write side runs through
/// <see cref="BusinessWriteFlowClient.PostVendorCreditAsync"/>; this
/// client only reads. Detail uses the existing /vendor-credits/{id}
/// endpoint; list uses the new /vendor-credits endpoint added with
/// the V1 list rollout.
/// </summary>
public sealed class VendorCreditClient(HttpClient httpClient, ILogger<VendorCreditClient> logger)
{
    public async Task<IReadOnlyList<VendorCreditSummaryDto>> ListAsync(
        Guid companyId,
        bool includeDrafts = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/vendor-credits?companyId={companyId:D}&includeDrafts={(includeDrafts ? "true" : "false")}";
            var rows = await httpClient.GetFromJsonAsync<VendorCreditSummaryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<VendorCreditSummaryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list vendor credits.");
            return Array.Empty<VendorCreditSummaryDto>();
        }
    }

    public async Task<VendorCreditRecordDto?> GetByIdAsync(
        Guid documentId,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"accounting/vendor-credits/{documentId:D}?CompanyId={companyId:D}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<VendorCreditRecordDto>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load vendor credit {DocumentId}.", documentId);
            return null;
        }
    }
}

public sealed record VendorCreditSummaryDto(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DocumentDate,
    Guid VendorId,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    DateTimeOffset? PostedAt);

public sealed record VendorCreditRecordDto(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    DateOnly DocumentDate,
    DateOnly DueDate,
    Guid VendorId,
    Guid PayableAccountId,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal SubtotalAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    IReadOnlyList<VendorCreditLineDto> Lines);

public sealed record VendorCreditLineDto(
    int LineNumber,
    Guid ExpenseAccountId,
    string Description,
    decimal LineAmount,
    decimal TaxAmount,
    bool IsTaxRecoverable,
    Guid? RecoverableTaxAccountId);
