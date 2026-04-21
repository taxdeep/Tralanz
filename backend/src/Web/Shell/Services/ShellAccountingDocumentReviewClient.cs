using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Web.Shell.Services;

public sealed class ShellAccountingDocumentReviewClient(HttpClient httpClient, ILogger<ShellAccountingDocumentReviewClient> logger)
{
    public async Task<WebShellAuthenticatedApiResult<ShellAccountingDocumentReviewSummary>> GetDocumentAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildApiPath(sourceType, documentId, out var requestPath))
        {
            logger.LogInformation("Unsupported accounting document review source type {SourceType}.", sourceType);
            return WebShellAuthenticatedApiResult<ShellAccountingDocumentReviewSummary>.Failure(
                "Unsupported accounting document review source type.");
        }

        return await GetOptionalAsync<ShellAccountingDocumentReviewSummary>(
            $"{requestPath}?companyId={companyId:D}",
            "accounting document review",
            sourceType,
            documentId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellPurchaseOrderReviewSummary>> GetPurchaseOrderAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        await GetOptionalAsync<ShellPurchaseOrderReviewSummary>(
            $"accounting/purchase-orders/{documentId:D}?companyId={companyId:D}",
            "purchase order review",
            "purchase_order",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<IReadOnlyList<ShellPurchaseOrderLifecycleAuditEntry>>> ListPurchaseOrderLifecycleAuditAsync(
        Guid companyId,
        Guid documentId,
        int take = 50,
        CancellationToken cancellationToken = default) =>
        await GetListAsync<ShellPurchaseOrderLifecycleAuditEntry>(
            $"accounting/purchase-orders/{documentId:D}/lifecycle-audit?companyId={companyId:D}&take={Math.Clamp(take, 1, 200)}",
            "purchase order lifecycle audit",
            "purchase_order",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<IReadOnlyList<ShellPurchaseOrderApprovalRequestSummary>>> ListPurchaseOrderApprovalRequestsAsync(
        Guid companyId,
        int take = 50,
        bool includeClosed = false,
        CancellationToken cancellationToken = default) =>
        await GetListAsync<ShellPurchaseOrderApprovalRequestSummary>(
            $"accounting/purchase-orders/approval-requests?companyId={companyId:D}&take={Math.Clamp(take, 1, 200)}&includeClosed={includeClosed.ToString().ToLowerInvariant()}",
            "purchase order approval requests",
            "purchase_order",
            Guid.Empty,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellPurchaseOrderApprovalRequestSummary>> GetLatestPurchaseOrderApprovalRequestAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        await GetOptionalAsync<ShellPurchaseOrderApprovalRequestSummary>(
            $"accounting/purchase-orders/{documentId:D}/approval-request?companyId={companyId:D}",
            "purchase order approval request",
            "purchase_order",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellPurchaseOrderApprovalRequestCommandResultSummary>> RequestPurchaseOrderApprovalAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderApprovalCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/approval-request",
            new PurchaseOrderApprovalRequestCommand(companyId, userId, reason),
            "request purchase order approval",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellPurchaseOrderApprovalRequestCommandResultSummary>> SubmitPurchaseOrderApprovalRequestAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderApprovalCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/approval-request/{requestId:D}/submit",
            new PurchaseOrderApprovalRequestTransitionCommand(companyId, userId),
            "submit purchase order approval request",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellPurchaseOrderApprovalRequestCommandResultSummary>> RejectPurchaseOrderApprovalRequestAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderApprovalCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/approval-request/{requestId:D}/reject",
            new PurchaseOrderApprovalRequestTransitionCommand(companyId, userId),
            "reject purchase order approval request",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> ApprovePurchaseOrderAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderLifecycleCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/approve",
            new PurchaseOrderApprovalRequestTransitionCommand(companyId, userId),
            "approve purchase order",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> ReversePurchaseOrderApprovalAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderLifecycleCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/approval/reverse",
            new PurchaseOrderApprovalRequestTransitionCommand(companyId, userId),
            "reverse purchase order approval",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> ReleasePurchaseOrderAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderLifecycleCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/issue",
            new PurchaseOrderApprovalRequestTransitionCommand(companyId, userId),
            "release purchase order",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> ReopenPurchaseOrderForAmendmentAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderLifecycleCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/reopen-for-amendment",
            new PurchaseOrderApprovalRequestTransitionCommand(companyId, userId),
            "reopen purchase order for amendment",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> ClosePurchaseOrderAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderLifecycleCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/close",
            new PurchaseOrderApprovalRequestTransitionCommand(companyId, userId),
            "close purchase order",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> CancelPurchaseOrderAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderLifecycleCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/cancel",
            new PurchaseOrderApprovalRequestTransitionCommand(companyId, userId),
            "cancel purchase order",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellPurchaseOrderThreeQuantitySummary>> RefreshPurchaseOrderQuantityDiscrepanciesAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderThreeQuantityCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/quantity-discrepancies/refresh",
            new PurchaseOrderApprovalRequestTransitionCommand(companyId, userId),
            "refresh purchase order quantity discrepancies",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellPurchaseOrderThreeQuantitySummary>> ReviewPurchaseOrderQuantityDiscrepancyAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        int purchaseOrderLineNumber,
        string discrepancyType,
        string investigationStatus,
        string? reviewNote,
        CancellationToken cancellationToken = default) =>
        await PostPurchaseOrderThreeQuantityCommandAsync(
            $"accounting/purchase-orders/{documentId:D}/quantity-discrepancies/review",
            new PurchaseOrderQuantityDiscrepancyReviewCommand(
                companyId,
                userId,
                purchaseOrderLineNumber,
                discrepancyType,
                investigationStatus,
                reviewNote),
            "review purchase order quantity discrepancy",
            documentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellReceiptGrIrBridgeSummary>> RefreshReceiptGrIrBridgeAsync(
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken = default) =>
        await PostReceiptGrIrBridgeCommandAsync(
            $"accounting/receipts/{receiptDocumentId:D}/grir-bridge/refresh",
            new ReceiptGrIrSettlementRefreshCommand(companyId, userId),
            "refresh receipt GR/IR bridge control",
            receiptDocumentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellReceiptGrIrApSettlementSummary>> RefreshReceiptGrIrSettlementAsync(
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken = default) =>
        await PostReceiptGrIrSettlementCommandAsync(
            $"accounting/receipts/{receiptDocumentId:D}/grir-settlement/refresh",
            new ReceiptGrIrSettlementRefreshCommand(companyId, userId),
            "refresh receipt GR/IR settlement control",
            receiptDocumentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellReceiptGrIrApSettlementSummary>> RefreshReceiptGrIrJournalReconciliationAsync(
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken = default) =>
        await PostReceiptGrIrSettlementCommandAsync(
            $"accounting/receipts/{receiptDocumentId:D}/grir-settlement/journal-reconciliation/refresh",
            new ReceiptGrIrSettlementRefreshCommand(companyId, userId),
            "refresh receipt GR/IR journal reconciliation",
            receiptDocumentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellReceiptGrIrApSettlementSummary>> RefreshReceiptPurchaseVarianceAsync(
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken = default) =>
        await PostReceiptGrIrSettlementCommandAsync(
            $"accounting/receipts/{receiptDocumentId:D}/grir-settlement/purchase-variance/refresh",
            new ReceiptGrIrSettlementRefreshCommand(companyId, userId),
            "refresh receipt purchase variance control",
            receiptDocumentId,
            cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseRequestSummary>> GetLatestReverseRequestAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildReverseRequestApiPath(sourceType, documentId, out var requestPath))
        {
            logger.LogInformation("Unsupported accounting document reverse request source type {SourceType}.", sourceType);
            return WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseRequestSummary>.Failure(
                "Unsupported accounting document reverse request source type.");
        }

        return await GetOptionalAsync<ShellAccountingDocumentReverseRequestSummary>(
            $"{requestPath}?companyId={companyId:D}",
            "accounting reverse request",
            sourceType,
            documentId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseCommandResultSummary>> RequestReverseAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildReverseApiPath(sourceType, documentId, out var requestPath))
        {
            logger.LogInformation("Unsupported accounting document reverse command source type {SourceType}.", sourceType);
            return WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseCommandResultSummary>.Failure(
                "Unsupported accounting document reverse command source type.");
        }

        return await PostReverseCommandAsync(
            $"{requestPath}?companyId={companyId:D}",
            "request reverse",
            sourceType,
            documentId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseCommandResultSummary>> SubmitReverseRequestAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildReverseRequestTransitionApiPath(sourceType, documentId, requestId, "submit", out var requestPath))
        {
            logger.LogInformation("Unsupported accounting document reverse submit source type {SourceType}.", sourceType);
            return WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseCommandResultSummary>.Failure(
                "Unsupported accounting document reverse submit source type.");
        }

        return await PostReverseCommandAsync(
            $"{requestPath}?companyId={companyId:D}",
            "submit reverse request",
            sourceType,
            documentId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseCommandResultSummary>> ExecuteReverseRequestAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildReverseRequestTransitionApiPath(sourceType, documentId, requestId, "execute", out var requestPath))
        {
            logger.LogInformation("Unsupported accounting document reverse execute source type {SourceType}.", sourceType);
            return WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseCommandResultSummary>.Failure(
                "Unsupported accounting document reverse execute source type.");
        }

        return await PostReverseCommandAsync(
            $"{requestPath}?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}",
            "execute reverse request",
            sourceType,
            documentId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseApplyReadinessSummary>> GetReverseRequestApplyReadinessAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildReverseRequestTransitionApiPath(sourceType, documentId, requestId, "apply-readiness", out var requestPath))
        {
            logger.LogInformation("Unsupported accounting document reverse readiness source type {SourceType}.", sourceType);
            return WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseApplyReadinessSummary>.Failure(
                "Unsupported accounting document reverse readiness source type.");
        }

        return await GetOptionalAsync<ShellAccountingDocumentReverseApplyReadinessSummary>(
            $"{requestPath}?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}",
            "reverse apply-readiness",
            sourceType,
            documentId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseExecutionPlanSummary>> GetReverseRequestExecutionPlanAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildReverseRequestTransitionApiPath(sourceType, documentId, requestId, "execution-plan", out var requestPath))
        {
            logger.LogInformation("Unsupported accounting document reverse execution plan source type {SourceType}.", sourceType);
            return WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseExecutionPlanSummary>.Failure(
                "Unsupported accounting document reverse execution plan source type.");
        }

        return await GetOptionalAsync<ShellAccountingDocumentReverseExecutionPlanSummary>(
            $"{requestPath}?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}",
            "reverse execution plan",
            sourceType,
            documentId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<IReadOnlyList<ShellSettlementApplicationReversalSummary>>> ListSettlementApplicationReversalsAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildSettlementApplicationReversalsApiPath(sourceType, documentId, out var requestPath))
        {
            logger.LogInformation("Unsupported settlement application reversal source type {SourceType}.", sourceType);
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellSettlementApplicationReversalSummary>>.Failure(
                "Unsupported settlement application reversal source type.");
        }

        return await GetListAsync<ShellSettlementApplicationReversalSummary>(
            $"{requestPath}?companyId={companyId:D}",
            "settlement application reversals",
            sourceType,
            documentId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<IReadOnlyList<ShellSubledgerReverseBlockerSummary>>> ListSubledgerReverseBlockersAsync(
        Guid companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildSubledgerReverseBlockersApiPath(sourceType, documentId, out var requestPath))
        {
            logger.LogInformation("Unsupported subledger reverse blocker source type {SourceType}.", sourceType);
            return WebShellAuthenticatedApiResult<IReadOnlyList<ShellSubledgerReverseBlockerSummary>>.Failure(
                "Unsupported subledger reverse blocker source type.");
        }

        return await GetListAsync<ShellSubledgerReverseBlockerSummary>(
            $"{requestPath}?companyId={companyId:D}",
            "subledger reverse blockers",
            sourceType,
            documentId,
            cancellationToken);
    }

    private async Task<WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseCommandResultSummary>> PostReverseCommandAsync(
        string requestUri,
        string operationLabel,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsync(requestUri, content: null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseCommandResultSummary>.RequiresAuthentication();
            }

            var result = await response.Content.ReadFromJsonAsync<ShellAccountingDocumentReverseCommandResultSummary>(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Unable to {OperationLabel} for {SourceType} {DocumentId}. Status code {StatusCode}.",
                    operationLabel,
                    sourceType,
                    documentId,
                    response.StatusCode);
            }

            return WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseCommandResultSummary>.Success(
                result ?? new ShellAccountingDocumentReverseCommandResultSummary
                {
                    OutcomeCode = response.IsSuccessStatusCode ? "accepted" : "request_failed",
                    Message = response.IsSuccessStatusCode
                        ? "The reverse command completed, but no response body was returned."
                        : $"The reverse command returned HTTP {(int)response.StatusCode}."
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to {OperationLabel} for {SourceType} {DocumentId}.", operationLabel, sourceType, documentId);
            return WebShellAuthenticatedApiResult<ShellAccountingDocumentReverseCommandResultSummary>.Failure(
                $"Unable to {operationLabel}. Check API availability and business-session headers.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<ShellPurchaseOrderApprovalRequestCommandResultSummary>> PostPurchaseOrderApprovalCommandAsync<TRequest>(
        string requestUri,
        TRequest request,
        string operationLabel,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestUri, request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellPurchaseOrderApprovalRequestCommandResultSummary>.RequiresAuthentication();
            }

            if (!response.IsSuccessStatusCode)
            {
                return WebShellAuthenticatedApiResult<ShellPurchaseOrderApprovalRequestCommandResultSummary>.Failure(
                    await ReadErrorMessageAsync(response, cancellationToken));
            }

            var result = await response.Content.ReadFromJsonAsync<ShellPurchaseOrderApprovalRequestCommandResultSummary>(cancellationToken);
            return result is null
                ? WebShellAuthenticatedApiResult<ShellPurchaseOrderApprovalRequestCommandResultSummary>.Failure(
                    $"{operationLabel} succeeded but returned an empty payload.")
                : WebShellAuthenticatedApiResult<ShellPurchaseOrderApprovalRequestCommandResultSummary>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to {OperationLabel} for purchase order {DocumentId}.", operationLabel, documentId);
            return WebShellAuthenticatedApiResult<ShellPurchaseOrderApprovalRequestCommandResultSummary>.Failure(
                $"Unable to {operationLabel}. Check API availability and business-session headers.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> PostPurchaseOrderLifecycleCommandAsync<TRequest>(
        string requestUri,
        TRequest request,
        string operationLabel,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestUri, request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.RequiresAuthentication();
            }

            if (!response.IsSuccessStatusCode)
            {
                return WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Failure(
                    await ReadErrorMessageAsync(response, cancellationToken));
            }

            var result = await response.Content.ReadFromJsonAsync<ShellSourceDocumentDraftSaveResult>(cancellationToken);
            return result is null
                ? WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Failure(
                    $"{operationLabel} succeeded but returned an empty payload.")
                : WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to {OperationLabel} for purchase order {DocumentId}.", operationLabel, documentId);
            return WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Failure(
                $"Unable to {operationLabel}. Check API availability and business-session headers.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<ShellPurchaseOrderThreeQuantitySummary>> PostPurchaseOrderThreeQuantityCommandAsync<TRequest>(
        string requestUri,
        TRequest request,
        string operationLabel,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestUri, request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellPurchaseOrderThreeQuantitySummary>.RequiresAuthentication();
            }

            if (!response.IsSuccessStatusCode)
            {
                return WebShellAuthenticatedApiResult<ShellPurchaseOrderThreeQuantitySummary>.Failure(
                    await ReadErrorMessageAsync(response, cancellationToken));
            }

            var result = await response.Content.ReadFromJsonAsync<ShellPurchaseOrderThreeQuantitySummary>(cancellationToken);
            return result is null
                ? WebShellAuthenticatedApiResult<ShellPurchaseOrderThreeQuantitySummary>.Failure(
                    $"{operationLabel} succeeded but returned an empty payload.")
                : WebShellAuthenticatedApiResult<ShellPurchaseOrderThreeQuantitySummary>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to {OperationLabel} for purchase order {DocumentId}.", operationLabel, documentId);
            return WebShellAuthenticatedApiResult<ShellPurchaseOrderThreeQuantitySummary>.Failure(
                $"Unable to {operationLabel}. Check API availability and business-session headers.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<ShellReceiptGrIrBridgeSummary>> PostReceiptGrIrBridgeCommandAsync<TRequest>(
        string requestUri,
        TRequest request,
        string operationLabel,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestUri, request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellReceiptGrIrBridgeSummary>.RequiresAuthentication();
            }

            if (!response.IsSuccessStatusCode)
            {
                return WebShellAuthenticatedApiResult<ShellReceiptGrIrBridgeSummary>.Failure(
                    await ReadErrorMessageAsync(response, cancellationToken));
            }

            var result = await response.Content.ReadFromJsonAsync<ShellReceiptGrIrBridgeSummary>(cancellationToken);
            return result is null
                ? WebShellAuthenticatedApiResult<ShellReceiptGrIrBridgeSummary>.Failure(
                    $"{operationLabel} succeeded but returned an empty payload.")
                : WebShellAuthenticatedApiResult<ShellReceiptGrIrBridgeSummary>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to {OperationLabel} for receipt {ReceiptDocumentId}.", operationLabel, receiptDocumentId);
            return WebShellAuthenticatedApiResult<ShellReceiptGrIrBridgeSummary>.Failure(
                $"Unable to {operationLabel}. Check API availability and business-session headers.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<ShellReceiptGrIrApSettlementSummary>> PostReceiptGrIrSettlementCommandAsync<TRequest>(
        string requestUri,
        TRequest request,
        string operationLabel,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestUri, request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellReceiptGrIrApSettlementSummary>.RequiresAuthentication();
            }

            if (!response.IsSuccessStatusCode)
            {
                return WebShellAuthenticatedApiResult<ShellReceiptGrIrApSettlementSummary>.Failure(
                    await ReadErrorMessageAsync(response, cancellationToken));
            }

            var result = await response.Content.ReadFromJsonAsync<ShellReceiptGrIrApSettlementSummary>(cancellationToken);
            return result is null
                ? WebShellAuthenticatedApiResult<ShellReceiptGrIrApSettlementSummary>.Failure(
                    $"{operationLabel} succeeded but returned an empty payload.")
                : WebShellAuthenticatedApiResult<ShellReceiptGrIrApSettlementSummary>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to {OperationLabel} for receipt {ReceiptDocumentId}.", operationLabel, receiptDocumentId);
            return WebShellAuthenticatedApiResult<ShellReceiptGrIrApSettlementSummary>.Failure(
                $"Unable to {operationLabel}. Check API availability and business-session headers.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<T>> GetOptionalAsync<T>(
        string requestUri,
        string operationLabel,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<T>.RequiresAuthentication();
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return WebShellAuthenticatedApiResult<T>.NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                return WebShellAuthenticatedApiResult<T>.Failure(await ReadErrorMessageAsync(response, cancellationToken));
            }

            var payload = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
            return payload is null
                ? WebShellAuthenticatedApiResult<T>.Failure($"{operationLabel} succeeded but returned an empty payload.")
                : WebShellAuthenticatedApiResult<T>.Success(payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load {OperationLabel} for {SourceType} {DocumentId}.", operationLabel, sourceType, documentId);
            return WebShellAuthenticatedApiResult<T>.Failure(
                $"Unable to load {operationLabel}. Check API availability and business-session headers.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<IReadOnlyList<TItem>>> GetListAsync<TItem>(
        string requestUri,
        string operationLabel,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<IReadOnlyList<TItem>>.RequiresAuthentication();
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return WebShellAuthenticatedApiResult<IReadOnlyList<TItem>>.Success(Array.Empty<TItem>());
            }

            if (!response.IsSuccessStatusCode)
            {
                return WebShellAuthenticatedApiResult<IReadOnlyList<TItem>>.Failure(await ReadErrorMessageAsync(response, cancellationToken));
            }

            var payload = await response.Content.ReadFromJsonAsync<TItem[]>(cancellationToken);
            return WebShellAuthenticatedApiResult<IReadOnlyList<TItem>>.Success(payload ?? Array.Empty<TItem>());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load {OperationLabel} for {SourceType} {DocumentId}.", operationLabel, sourceType, documentId);
            return WebShellAuthenticatedApiResult<IReadOnlyList<TItem>>.Failure(
                $"Unable to load {operationLabel}. Check API availability and business-session headers.");
        }
    }

    private static async Task<string> ReadErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return WebShellBusinessSessionClient.AuthenticationRequiredError;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "The requested accounting document resource was not found in the active company context.";
        }

        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            if (payload?["message"]?.GetValue<string>() is { Length: > 0 } message)
            {
                return message;
            }

            if (payload?["error"]?.GetValue<string>() is { Length: > 0 } error)
            {
                return error;
            }
        }
        catch
        {
        }

        return $"Accounting document review returned HTTP {(int)response.StatusCode}.";
    }

    private static bool TryBuildApiPath(string? sourceType, Guid documentId, out string requestPath)
    {
        var normalized = Normalize(sourceType);
        requestPath = normalized is null
            ? string.Empty
            : $"accounting/document-review/{normalized}/{documentId:D}";

        return normalized is not null;
    }

    private static bool TryBuildReverseApiPath(string? sourceType, Guid documentId, out string requestPath)
    {
        var normalized = Normalize(sourceType);
        requestPath = normalized is null
            ? string.Empty
            : $"accounting/source-document-lifecycle/{normalized}/{documentId:D}/reverse";

        return normalized is not null;
    }

    private static bool TryBuildReverseRequestApiPath(string? sourceType, Guid documentId, out string requestPath)
    {
        var normalized = Normalize(sourceType);
        requestPath = normalized is null
            ? string.Empty
            : $"accounting/source-document-lifecycle/{normalized}/{documentId:D}/reverse-request";

        return normalized is not null;
    }

    private static bool TryBuildReverseRequestTransitionApiPath(
        string? sourceType,
        Guid documentId,
        Guid requestId,
        string transition,
        out string requestPath)
    {
        var normalized = Normalize(sourceType);
        requestPath = normalized is null
            ? string.Empty
            : $"accounting/source-document-lifecycle/{normalized}/{documentId:D}/reverse-request/{requestId:D}/{transition}";

        return normalized is not null;
    }

    private static bool TryBuildSubledgerReverseBlockersApiPath(string? sourceType, Guid documentId, out string requestPath)
    {
        var normalized = Normalize(sourceType);
        requestPath = normalized is null
            ? string.Empty
            : $"accounting/source-document-lifecycle/{normalized}/{documentId:D}/reverse-blockers";

        return normalized is not null;
    }

    private static bool TryBuildSettlementApplicationReversalsApiPath(string? sourceType, Guid documentId, out string requestPath)
    {
        var normalized = Normalize(sourceType);
        requestPath = normalized is null
            ? string.Empty
            : $"accounting/source-document-lifecycle/{normalized}/{documentId:D}/settlement-application-reversals";

        return normalized is not null;
    }

    private static string? Normalize(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return null;
        }

        return sourceType.Trim().ToLowerInvariant() switch
        {
            "invoice" => "invoice",
            "credit_note" => "credit_note",
            "bill" => "bill",
            "vendor_credit" => "vendor_credit",
            "receive_payment" => "receive_payment",
            "credit_application" => "credit_application",
            "pay_bill" => "pay_bill",
            "vendor_credit_application" => "vendor_credit_application",
            _ => null
        };
    }

    private sealed record PurchaseOrderApprovalRequestCommand(
        Guid CompanyId,
        Guid UserId,
        string? Reason);

    private sealed record PurchaseOrderApprovalRequestTransitionCommand(
        Guid CompanyId,
        Guid UserId);

    private sealed record PurchaseOrderQuantityDiscrepancyReviewCommand(
        Guid CompanyId,
        Guid UserId,
        int PurchaseOrderLineNumber,
        string DiscrepancyType,
        string InvestigationStatus,
        string? ReviewNote);

    private sealed record ReceiptGrIrSettlementRefreshCommand(
        Guid CompanyId,
        Guid UserId);
}
