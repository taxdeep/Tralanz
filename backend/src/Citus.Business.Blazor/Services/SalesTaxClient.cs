using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

public sealed class SalesTaxClient(HttpClient httpClient, ILogger<SalesTaxClient> logger)
{
    public async Task<IReadOnlyList<SalesTaxRuleSummary>> ListRulesAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = includeInactive ? "accounting/sales-tax/rules?includeInactive=true" : "accounting/sales-tax/rules";
            var rows = await httpClient.GetFromJsonAsync<SalesTaxRuleSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<SalesTaxRuleSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read sales tax rules.");
            return Array.Empty<SalesTaxRuleSummary>();
        }
    }

    public async Task<IReadOnlyList<SalesTaxCodeSummary>> ListCodesAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = includeInactive ? "accounting/sales-tax/codes?includeInactive=true" : "accounting/sales-tax/codes";
            var rows = await httpClient.GetFromJsonAsync<SalesTaxCodeSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<SalesTaxCodeSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read sales tax codes.");
            return Array.Empty<SalesTaxCodeSummary>();
        }
    }

    public async Task<SalesTaxCodeMutationOutcome> CreateCodeAsync(
        SalesTaxCodeUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendCodeUpsertAsync(HttpMethod.Post, "accounting/sales-tax/codes", payload, cancellationToken);

    public async Task<SalesTaxCodeMutationOutcome> UpdateCodeAsync(
        Guid id,
        SalesTaxCodeUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendCodeUpsertAsync(HttpMethod.Put, $"accounting/sales-tax/codes/{id:D}", payload, cancellationToken);

    public async Task<SalesTaxRuleMutationOutcome> CreateRuleAsync(
        SalesTaxRuleUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendRuleUpsertAsync(HttpMethod.Post, "accounting/sales-tax/rules", payload, cancellationToken);

    public async Task<SalesTaxRuleMutationOutcome> UpdateRuleAsync(
        Guid id,
        SalesTaxRuleUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendRuleUpsertAsync(HttpMethod.Put, $"accounting/sales-tax/rules/{id:D}", payload, cancellationToken);

    public async Task<SalesTaxPreviewResult?> CalculatePreviewAsync(
        SalesTaxPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "accounting/sales-tax/calculate-preview",
                request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SalesTaxPreviewResult>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to calculate sales tax preview.");
            return null;
        }
    }

    public async Task<IReadOnlyList<SalesTaxReportSummaryRow>> GetSummaryReportAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/sales-tax/reports/summary?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
            var rows = await httpClient.GetFromJsonAsync<SalesTaxReportSummaryRow[]>(url, cancellationToken);
            return rows ?? Array.Empty<SalesTaxReportSummaryRow>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read sales tax summary.");
            return Array.Empty<SalesTaxReportSummaryRow>();
        }
    }

    private async Task<SalesTaxCodeMutationOutcome> SendCodeUpsertAsync(
        HttpMethod method,
        string path,
        SalesTaxCodeUpsertPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(payload),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadMessageAsync(response, cancellationToken);
                return new SalesTaxCodeMutationOutcome(false, null, error);
            }

            var saved = await response.Content.ReadFromJsonAsync<SalesTaxCodeSummary>(cancellationToken);
            return new SalesTaxCodeMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert sales tax code.");
            return new SalesTaxCodeMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<SalesTaxRuleMutationOutcome> SendRuleUpsertAsync(
        HttpMethod method,
        string path,
        SalesTaxRuleUpsertPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(payload),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadMessageAsync(response, cancellationToken);
                return new SalesTaxRuleMutationOutcome(false, null, error);
            }

            var saved = await response.Content.ReadFromJsonAsync<SalesTaxRuleSummary>(cancellationToken);
            return new SalesTaxRuleMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert sales tax rule.");
            return new SalesTaxRuleMutationOutcome(false, null, "Unable to reach the server. Please try again.");
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
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return message.GetString() ?? raw;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return raw;
    }
}

public sealed record SalesTaxRuleSummary(
    Guid Id,
    CompanyId CompanyId,
    Guid? AuthorityId,
    string Code,
    string Name,
    string TaxType,
    string AppliesTo,
    string Treatment,
    string Recoverability,
    decimal CurrentRatePercent,
    Guid? PayableAccountId,
    Guid? RecoverableAccountId,
    string? RegistrationNumber,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsActive);

public sealed record SalesTaxCodeSummary(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string Code,
    string Name,
    string Treatment,
    string AppliesTo,
    decimal SalesRatePercent,
    decimal PurchaseRatePercent,
    string? RegistrationNumber,
    bool IsGroup,
    bool IsActive,
    IReadOnlyList<SalesTaxCodeComponentSummary> Components,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SalesTaxCodeComponentSummary(
    Guid TaxCodeId,
    Guid? TaxComponentId,
    string Code,
    string Name,
    string TaxType,
    string AppliesTo,
    decimal RatePercent,
    int Sequence,
    string CompoundMode,
    string Treatment,
    string Recoverability,
    decimal RecoverablePercent,
    string? RegistrationNumber);

public sealed record SalesTaxCodeUpsertPayload(
    string Code,
    string Name,
    bool IsActive,
    IReadOnlyList<SalesTaxCodeComponentUpsertPayload> Components);

public sealed record SalesTaxCodeComponentUpsertPayload(
    decimal RatePercent,
    string TaxType,
    string Recoverability,
    string AppliesTo,
    string? RegistrationNumber,
    Guid? TaxRuleId = null);

public sealed record SalesTaxCodeMutationOutcome(
    bool Succeeded,
    SalesTaxCodeSummary? Saved,
    string? ErrorMessage);

public sealed record SalesTaxRuleUpsertPayload(
    string Code,
    string Name,
    decimal RatePercent,
    string TaxType,
    string AppliesTo,
    string Treatment,
    string Recoverability,
    Guid? PayableAccountId,
    Guid? RecoverableAccountId,
    string? RegistrationNumber,
    bool IsActive);

public sealed record SalesTaxRuleMutationOutcome(
    bool Succeeded,
    SalesTaxRuleSummary? Saved,
    string? ErrorMessage);

public sealed record SalesTaxPreviewRequest(
    string DocumentSide,
    DateOnly DocumentDate,
    string TaxMode,
    decimal Amount,
    Guid? TaxCodeId,
    string CurrencyCode);

public sealed record SalesTaxPreviewLine(
    Guid? TaxCodeId,
    Guid? TaxComponentId,
    string Code,
    string Name,
    decimal TaxableAmount,
    decimal RatePercent,
    decimal TaxAmount,
    decimal RecoverableAmount,
    decimal NonRecoverableAmount,
    string Treatment,
    string Recoverability,
    string ReportingCategory);

public sealed record SalesTaxPreviewResult(
    decimal TaxableAmount,
    decimal TaxAmount,
    decimal RecoverableAmount,
    decimal NonRecoverableAmount,
    decimal GrossAmount,
    string CurrencyCode,
    IReadOnlyList<SalesTaxPreviewLine> Lines);

public sealed record SalesTaxReportSummaryRow(
    string JurisdictionCode,
    string RegistrationNumber,
    string TaxComponentCode,
    string ReportingCategory,
    decimal TaxableAmount,
    decimal TaxCollected,
    decimal InputTaxRecoverable,
    decimal NonRecoverableTax,
    decimal NetTax);
