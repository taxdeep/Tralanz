using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class InvoiceTemplateClient(
    HttpClient httpClient,
    ILogger<InvoiceTemplateClient> logger)
{
    public async Task<IReadOnlyList<InvoiceTemplateDto>> ListAsync(
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await httpClient.GetFromJsonAsync<InvoiceTemplateDto[]>(
                $"accounting/invoice-templates?companyId={companyId:D}",
                cancellationToken);
            return rows ?? Array.Empty<InvoiceTemplateDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list invoice templates.");
            return Array.Empty<InvoiceTemplateDto>();
        }
    }

    public async Task<InvoiceTemplateDto?> GetAsync(
        Guid companyId,
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<InvoiceTemplateDto>(
                $"accounting/invoice-templates/{templateId:D}?companyId={companyId:D}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load invoice template {TemplateId}.", templateId);
            return null;
        }
    }

    public async Task<InvoiceTemplateOutcome> CreateAsync(
        Guid companyId,
        InvoiceTemplateUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        return await UpsertAsync(
            HttpMethod.Post,
            $"accounting/invoice-templates?companyId={companyId:D}",
            request,
            cancellationToken);
    }

    public async Task<InvoiceTemplateOutcome> UpdateAsync(
        Guid companyId,
        Guid templateId,
        InvoiceTemplateUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        return await UpsertAsync(
            HttpMethod.Put,
            $"accounting/invoice-templates/{templateId:D}?companyId={companyId:D}",
            request,
            cancellationToken);
    }

    /// <summary>
    /// Renders a PDF preview of the unsaved draft template against a
    /// synthetic sample invoice. The endpoint owns the sample shape;
    /// caller just hands over what would have been a save body.
    /// Returns null on any non-success status / network failure so the
    /// editor can leave the previous preview visible while typing.
    /// </summary>
    public async Task<byte[]?> GeneratePreviewAsync(
        Guid companyId,
        InvoiceTemplateUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/invoice-templates/preview-pdf?companyId={companyId:D}",
                request,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invoice template preview render failed.");
            return null;
        }
    }

    public async Task<InvoiceTemplateOutcome> SetDefaultAsync(
        Guid companyId,
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                $"accounting/invoice-templates/{templateId:D}/set-default?companyId={companyId:D}",
                content: null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return new InvoiceTemplateOutcome(false, errorBody, null);
            }

            var saved = await response.Content.ReadFromJsonAsync<InvoiceTemplateDto>(cancellationToken);
            return new InvoiceTemplateOutcome(true, null, saved);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to set invoice template {TemplateId} as default.", templateId);
            return new InvoiceTemplateOutcome(false, ex.Message, null);
        }
    }

    private async Task<InvoiceTemplateOutcome> UpsertAsync(
        HttpMethod method,
        string requestUri,
        InvoiceTemplateUpsertDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpRequestMessage(method, requestUri)
            {
                Content = JsonContent.Create(request),
            };
            using var response = await httpClient.SendAsync(http, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new InvoiceTemplateOutcome(false, body, null);
            }

            var saved = await response.Content.ReadFromJsonAsync<InvoiceTemplateDto>(cancellationToken);
            return new InvoiceTemplateOutcome(true, null, saved);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invoice template upsert failed.");
            return new InvoiceTemplateOutcome(false, ex.Message, null);
        }
    }
}

public sealed record InvoiceTemplateDto(
    Guid Id,
    Guid CompanyId,
    string Name,
    bool IsDefault,
    string? LogoUrl,
    string PrimaryColorHex,
    string AccentColorHex,
    string? Tagline,
    string Greeting,
    string PaymentInstructions,
    string FooterNote,
    bool ShowTaxColumn,
    string EmailSubjectTemplate,
    string EmailBodyTemplate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record InvoiceTemplateUpsertDto(
    string Name,
    string? LogoUrl,
    string PrimaryColorHex,
    string AccentColorHex,
    string? Tagline,
    string Greeting,
    string PaymentInstructions,
    string FooterNote,
    bool ShowTaxColumn,
    string EmailSubjectTemplate,
    string EmailBodyTemplate);

public sealed record InvoiceTemplateOutcome(
    bool Succeeded,
    string? ErrorMessage,
    InvoiceTemplateDto? Saved);
