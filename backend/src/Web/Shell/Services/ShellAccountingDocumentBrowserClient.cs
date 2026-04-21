using System.Net.Http.Json;

namespace Web.Shell.Services;

public sealed class ShellAccountingDocumentBrowserClient(HttpClient httpClient, ILogger<ShellAccountingDocumentBrowserClient> logger)
{
    public async Task<WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>> GetDocumentsAsync(
        Guid companyId,
        string? sourceType,
        string? counterpartyRole,
        Guid? counterpartyId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(sourceType?.Trim(), "purchase_order", StringComparison.OrdinalIgnoreCase))
        {
            return await GetPurchaseOrdersAsync(companyId, counterpartyRole, counterpartyId, limit, cancellationToken);
        }

        var requestUri = $"accounting/documents/source?companyId={companyId:D}&limit={Math.Clamp(limit, 1, 200)}";
        if (!string.IsNullOrWhiteSpace(sourceType))
        {
            requestUri += $"&sourceType={Uri.EscapeDataString(sourceType.Trim().ToLowerInvariant())}";
        }
        if (!string.IsNullOrWhiteSpace(counterpartyRole))
        {
            requestUri += $"&counterpartyRole={Uri.EscapeDataString(counterpartyRole.Trim().ToLowerInvariant())}";
        }
        if (counterpartyId.HasValue)
        {
            requestUri += $"&counterpartyId={counterpartyId.Value:D}";
        }

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.RequiresAuthentication();
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorMessageAsync(response, cancellationToken);
                logger.LogWarning("Unable to load accounting source documents for company {CompanyId}: {Error}", companyId, error);
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Failure(error);
            }

            var items = await response.Content.ReadFromJsonAsync<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>(cancellationToken)
                ?? Array.Empty<ShellAccountingSourceDocumentBrowserItem>();
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Success(items);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load accounting source documents for company {CompanyId}.", companyId);
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Failure("Unable to load accounting source documents right now.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>> GetPurchaseOrdersAsync(
        Guid companyId,
        string? counterpartyRole,
        Guid? counterpartyId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.Equals(counterpartyRole?.Trim(), "customer", StringComparison.OrdinalIgnoreCase))
        {
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Success(
                Array.Empty<ShellAccountingSourceDocumentBrowserItem>());
        }

        var requestUri = $"accounting/purchase-orders?companyId={companyId:D}&take={Math.Clamp(limit, 1, 200)}";
        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.RequiresAuthentication();
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorMessageAsync(response, cancellationToken);
                logger.LogWarning("Unable to load purchase orders for company {CompanyId}: {Error}", companyId, error);
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Failure(error);
            }

            var items = await response.Content.ReadFromJsonAsync<IReadOnlyList<ShellPurchaseOrderBrowserItem>>(cancellationToken)
                ?? Array.Empty<ShellPurchaseOrderBrowserItem>();
            var mapped = items
                .Where(item => !counterpartyId.HasValue || item.VendorId == counterpartyId.Value)
                .Select(item => MapPurchaseOrder(companyId, item))
                .ToArray();
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Success(mapped);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load purchase orders for company {CompanyId}.", companyId);
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Failure("Unable to load purchase orders right now.");
        }
    }

    private static ShellAccountingSourceDocumentBrowserItem MapPurchaseOrder(
        Guid companyId,
        ShellPurchaseOrderBrowserItem item) =>
        new()
        {
            SourceType = "purchase_order",
            SourceTypeLabel = "Purchase Order",
            Id = item.DocumentId,
            CompanyId = companyId,
            EntityNumber = item.EntityNumber,
            DisplayNumber = item.DisplayNumber,
            Status = item.Status,
            DocumentDate = item.OrderDate,
            DueDate = item.ExpectedDate,
            CounterpartyLabel = "Vendor",
            CounterpartyId = item.VendorId,
            CounterpartyDisplayName = item.VendorId.ToString("D"),
            TotalOrderedQuantity = item.TotalOrderedQuantity,
            LineCount = item.LineCount,
            VendorReference = item.VendorReference,
            AnchorGovernanceSummary = item.AnchorGovernance?.Summary
        };

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ShellErrorPayload>(cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload?.Message))
            {
                return payload.Message;
            }
        }
        catch
        {
        }

        return $"Loading documents failed with HTTP {(int)response.StatusCode}.";
    }

    private sealed record class ShellErrorPayload(string? Message);

    private sealed record class ShellPurchaseOrderBrowserItem
    {
        public Guid DocumentId { get; init; }

        public string EntityNumber { get; init; } = string.Empty;

        public string DisplayNumber { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public Guid VendorId { get; init; }

        public DateOnly OrderDate { get; init; }

        public DateOnly? ExpectedDate { get; init; }

        public int LineCount { get; init; }

        public decimal TotalOrderedQuantity { get; init; }

        public string? VendorReference { get; init; }

        public ShellPurchaseOrderAnchorGovernanceSummary? AnchorGovernance { get; init; }
    }
}
