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

        if (string.Equals(sourceType?.Trim(), "receipt", StringComparison.OrdinalIgnoreCase))
        {
            return await GetReceiptsAsync(companyId, counterpartyRole, counterpartyId, limit, cancellationToken);
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
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.RequiresAuthentication(
                    "The source document service rejected the current business context. Your shell session is still active; refresh or retry after deployment completes.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                logger.LogWarning("Unable to load accounting source documents for company {CompanyId}: {Error}", companyId, error.Message);
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Failure(error.Message, error.Code);
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
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.RequiresAuthentication(
                    "The purchase order source service rejected the current business context. Your shell session is still active; refresh or retry after deployment completes.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                logger.LogWarning("Unable to load purchase orders for company {CompanyId}: {Error}", companyId, error.Message);
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Failure(error.Message, error.Code);
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

    private async Task<WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>> GetReceiptsAsync(
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

        var requestUri = $"accounting/receipts?companyId={companyId:D}&take={Math.Clamp(limit, 1, 200)}";
        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.RequiresAuthentication(
                    "The receipt source service rejected the current business context. Your shell session is still active; refresh or retry after deployment completes.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                logger.LogWarning("Unable to load receipts for company {CompanyId}: {Error}", companyId, error.Message);
                return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Failure(error.Message, error.Code);
            }

            var items = await response.Content.ReadFromJsonAsync<IReadOnlyList<ShellReceiptBrowserItem>>(cancellationToken)
                ?? Array.Empty<ShellReceiptBrowserItem>();
            var mapped = items
                .Where(item => !counterpartyId.HasValue || item.VendorId == counterpartyId.Value)
                .Select(item => MapReceipt(companyId, item))
                .ToArray();
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Success(mapped);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load receipts for company {CompanyId}.", companyId);
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellAccountingSourceDocumentBrowserItem>>.Failure("Unable to load receipts right now.");
        }
    }

    private static ShellAccountingSourceDocumentBrowserItem MapReceipt(
        Guid companyId,
        ShellReceiptBrowserItem item) =>
        new()
        {
            SourceType = "receipt",
            SourceTypeLabel = "Receipt",
            Id = item.DocumentId,
            CompanyId = companyId,
            EntityNumber = item.EntityNumber,
            DisplayNumber = item.DisplayNumber,
            Status = item.Status,
            DocumentDate = item.ReceiptDate,
            CounterpartyLabel = "Vendor",
            CounterpartyId = item.VendorId,
            CounterpartyDisplayName = item.VendorId.ToString("D"),
            TotalOrderedQuantity = item.TotalQuantity,
            LineCount = item.LineCount,
            VendorReference = item.VendorReference,
            ReceiptActivationStatus = item.InventoryActivation?.ActivationStatus,
            ReceiptValuationStatus = item.InventoryValuation?.ValuationStatus,
            ReceiptCostLayerEmissionStatus = item.InventoryCostLayerEmission?.EmissionStatus,
            ReceiptGrIrBridgeStatus = item.GrIrBridge?.BridgeStatus,
            ReceiptGrIrSettlementStatus = item.GrIrSettlement?.SettlementStatus,
            ReceiptPurchaseVarianceStatus = item.GrIrSettlement?.PurchaseVarianceStatus
        };

    private static async Task<ShellErrorPayload> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ShellErrorPayload>(cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload?.Message))
            {
                return payload;
            }
        }
        catch
        {
        }

        return new ShellErrorPayload(null, $"Loading documents failed with HTTP {(int)response.StatusCode}.");
    }

    private sealed record class ShellErrorPayload(string? Code, string Message);

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

    private sealed record class ShellReceiptBrowserItem
    {
        public Guid DocumentId { get; init; }

        public string EntityNumber { get; init; } = string.Empty;

        public string DisplayNumber { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public Guid VendorId { get; init; }

        public Guid WarehouseId { get; init; }

        public DateOnly ReceiptDate { get; init; }

        public int LineCount { get; init; }

        public decimal TotalQuantity { get; init; }

        public string? VendorReference { get; init; }

        public string? SourceReference { get; init; }

        public ShellReceiptInventoryActivationSummary? InventoryActivation { get; init; }

        public ShellReceiptInventoryValuationSummary? InventoryValuation { get; init; }

        public ShellReceiptInventoryCostLayerEmissionSummary? InventoryCostLayerEmission { get; init; }

        public ShellReceiptGrIrBridgeBrowserSummary? GrIrBridge { get; init; }

        public ShellReceiptGrIrSettlementBrowserSummary? GrIrSettlement { get; init; }
    }

    private sealed record class ShellReceiptInventoryActivationSummary
    {
        public string ActivationStatus { get; init; } = string.Empty;
    }

    private sealed record class ShellReceiptInventoryValuationSummary
    {
        public string ValuationStatus { get; init; } = string.Empty;
    }

    private sealed record class ShellReceiptInventoryCostLayerEmissionSummary
    {
        public string EmissionStatus { get; init; } = string.Empty;
    }

    private sealed record class ShellReceiptGrIrBridgeBrowserSummary
    {
        public string BridgeStatus { get; init; } = string.Empty;
    }

    private sealed record class ShellReceiptGrIrSettlementBrowserSummary
    {
        public string SettlementStatus { get; init; } = string.Empty;

        public string PurchaseVarianceStatus { get; init; } = string.Empty;
    }
}
