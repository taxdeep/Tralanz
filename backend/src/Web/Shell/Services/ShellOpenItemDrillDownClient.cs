using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Web.Shell.Services;

public sealed class ShellOpenItemDrillDownClient(HttpClient httpClient, ILogger<ShellOpenItemDrillDownClient> logger)
{
    public async Task<WebShellAuthenticatedApiResult<ShellOpenItemDrillDownResponse>> GetAsync(
        Guid companyId,
        string openItemType,
        Guid openItemId,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildPath(openItemType, openItemId, out var requestPath))
        {
            logger.LogInformation("Unsupported open item drill-down type {OpenItemType}.", openItemType);
            return WebShellAuthenticatedApiResult<ShellOpenItemDrillDownResponse>.Failure(
                "Unsupported open item drill-down type.");
        }

        return await GetOptionalAsync<ShellOpenItemDrillDownResponse>(
            $"{requestPath}?companyId={companyId:D}",
            "open item drill-down",
            openItemType,
            openItemId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentPreviewSummary>> GetAdjustmentPreviewAsync(
        Guid companyId,
        string openItemType,
        Guid openItemId,
        string adjustmentType,
        DateOnly adjustmentDate,
        decimal? adjustmentAmountTx,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildAdjustmentPath(openItemType, openItemId, "adjustment-preview", out var requestPath))
        {
            logger.LogInformation("Unsupported open item adjustment preview type {OpenItemType}.", openItemType);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentPreviewSummary>.Failure(
                "Unsupported open item adjustment preview type.");
        }

        var requestUri = $"{requestPath}?companyId={companyId:D}&adjustmentType={Uri.EscapeDataString(adjustmentType)}&adjustmentDate={adjustmentDate:yyyy-MM-dd}";
        if (adjustmentAmountTx.HasValue)
        {
            requestUri += $"&adjustmentAmountTx={adjustmentAmountTx.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return await GetOptionalAsync<ShellOpenItemAdjustmentPreviewSummary>(
            requestUri,
            "open item adjustment preview",
            openItemType,
            openItemId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentRequestSummary>> GetLatestAdjustmentRequestAsync(
        Guid companyId,
        string openItemType,
        Guid openItemId,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildAdjustmentPath(openItemType, openItemId, "adjustment-request", out var requestPath))
        {
            logger.LogInformation("Unsupported open item adjustment request type {OpenItemType}.", openItemType);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentRequestSummary>.Failure(
                "Unsupported open item adjustment request type.");
        }

        return await GetOptionalAsync<ShellOpenItemAdjustmentRequestSummary>(
            $"{requestPath}?companyId={companyId:D}",
            "latest open item adjustment request",
            openItemType,
            openItemId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentRequestAttemptSummary>> RequestAdjustmentAsync(
        Guid companyId,
        Guid? userId,
        string openItemType,
        Guid openItemId,
        string adjustmentType,
        DateOnly adjustmentDate,
        decimal? adjustmentAmountTx,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildAdjustmentPath(openItemType, openItemId, "adjustment-request", out var requestPath))
        {
            logger.LogInformation("Unsupported open item adjustment request type {OpenItemType}.", openItemType);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentRequestAttemptSummary>.Failure(
                "Unsupported open item adjustment request type.");
        }

        var body = new
        {
            CompanyId = companyId,
            UserId = userId,
            AdjustmentType = adjustmentType,
            AdjustmentDate = adjustmentDate,
            AdjustmentAmountTx = adjustmentAmountTx,
            Reason = reason
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestPath, body, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentRequestAttemptSummary>.RequiresAuthentication();
            }

            var result = await response.Content.ReadFromJsonAsync<ShellOpenItemAdjustmentRequestAttemptSummary>(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentRequestAttemptSummary>.Failure(
                    result?.Message ?? $"The adjustment request returned HTTP {(int)response.StatusCode}.",
                    string.IsNullOrWhiteSpace(result?.OutcomeCode) ? null : result.OutcomeCode);
            }

            return result is null
                ? WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentRequestAttemptSummary>.Failure(
                    "The adjustment request succeeded but returned an empty payload.")
                : WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentRequestAttemptSummary>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to request open item adjustment for {OpenItemType} {OpenItemId}.", openItemType, openItemId);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentRequestAttemptSummary>.Failure(
                "Unable to request the adjustment. Check API availability and business-session headers.");
        }
    }

    public Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>> SubmitAdjustmentRequestAsync(
        Guid companyId,
        Guid? userId,
        string openItemType,
        Guid openItemId,
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        PostAdjustmentTransitionAsync(companyId, userId, openItemType, openItemId, requestId, "submit", cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>> CancelAdjustmentRequestAsync(
        Guid companyId,
        Guid? userId,
        string openItemType,
        Guid openItemId,
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        PostAdjustmentTransitionAsync(companyId, userId, openItemType, openItemId, requestId, "cancel", cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>> ApproveAdjustmentRequestAsync(
        Guid companyId,
        Guid? userId,
        string openItemType,
        Guid openItemId,
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        PostAdjustmentTransitionAsync(companyId, userId, openItemType, openItemId, requestId, "approve", cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>> RejectAdjustmentRequestAsync(
        Guid companyId,
        Guid? userId,
        string openItemType,
        Guid openItemId,
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        PostAdjustmentTransitionAsync(companyId, userId, openItemType, openItemId, requestId, "reject", cancellationToken);

    public async Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentReadinessSummary>> GetAdjustmentReadinessAsync(
        Guid companyId,
        string openItemType,
        Guid openItemId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildAdjustmentRequestPath(openItemType, openItemId, requestId, "readiness", out var requestPath))
        {
            logger.LogInformation("Unsupported open item adjustment readiness type {OpenItemType}.", openItemType);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentReadinessSummary>.Failure(
                "Unsupported open item adjustment readiness type.");
        }

        return await GetOptionalAsync<ShellOpenItemAdjustmentReadinessSummary>(
            $"{requestPath}?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}",
            "open item adjustment readiness",
            openItemType,
            openItemId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentExecutionPlanSummary>> GetAdjustmentExecutionPlanAsync(
        Guid companyId,
        string openItemType,
        Guid openItemId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildAdjustmentRequestPath(openItemType, openItemId, requestId, "execution-plan", out var requestPath))
        {
            logger.LogInformation("Unsupported open item adjustment execution-plan type {OpenItemType}.", openItemType);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentExecutionPlanSummary>.Failure(
                "Unsupported open item adjustment execution-plan type.");
        }

        return await GetOptionalAsync<ShellOpenItemAdjustmentExecutionPlanSummary>(
            $"{requestPath}?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}",
            "open item adjustment execution plan",
            openItemType,
            openItemId,
            cancellationToken);
    }

    public async Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentExecutionResultSummary>> ExecuteAdjustmentRequestAsync(
        Guid companyId,
        Guid? userId,
        string openItemType,
        Guid openItemId,
        Guid requestId,
        Guid adjustmentAccountId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildAdjustmentRequestPath(openItemType, openItemId, requestId, "execute", out var requestPath))
        {
            logger.LogInformation("Unsupported open item adjustment execution type {OpenItemType}.", openItemType);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentExecutionResultSummary>.Failure(
                "Unsupported open item type for adjustment execution.");
        }

        var body = new
        {
            CompanyId = companyId,
            UserId = userId,
            AdjustmentAccountId = adjustmentAccountId,
            AsOfDate = asOfDate,
            IdempotencyKey = $"shell-open-item-adjustment:{companyId:D}:{requestId:D}"
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestPath, body, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentExecutionResultSummary>.RequiresAuthentication();
            }

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ShellOpenItemAdjustmentExecutionResultSummary>(cancellationToken);
                return result is null
                    ? WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentExecutionResultSummary>.Failure(
                        "Adjustment execution completed, but no response body was returned.")
                    : WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentExecutionResultSummary>.Success(result);
            }

            var error = await response.Content.ReadFromJsonAsync<ShellOpenItemAdjustmentErrorSummary>(cancellationToken);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentExecutionResultSummary>.Failure(
                string.IsNullOrWhiteSpace(error?.Message)
                    ? $"Adjustment execution returned HTTP {(int)response.StatusCode}."
                    : error.Message,
                error?.Code);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to execute open item adjustment for {OpenItemType} {OpenItemId}.", openItemType, openItemId);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentExecutionResultSummary>.Failure(
                "Unable to execute the adjustment. Check API availability and business-session headers.");
        }
    }

    private static bool TryBuildPath(string? openItemType, Guid openItemId, out string path)
    {
        path = openItemType?.Trim().ToLowerInvariant() switch
        {
            "ar" => $"accounting/open-items/ar/{openItemId:D}",
            "ap" => $"accounting/open-items/ap/{openItemId:D}",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(path);
    }

    private async Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>> PostAdjustmentTransitionAsync(
        Guid companyId,
        Guid? userId,
        string openItemType,
        Guid openItemId,
        Guid requestId,
        string transition,
        CancellationToken cancellationToken)
    {
        if (!TryBuildAdjustmentRequestPath(openItemType, openItemId, requestId, transition, out var requestPath))
        {
            logger.LogInformation("Unsupported open item adjustment transition type {OpenItemType}.", openItemType);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>.Failure(
                "Unsupported open item adjustment transition type.");
        }

        var body = new
        {
            CompanyId = companyId,
            UserId = userId
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestPath, body, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>.RequiresAuthentication();
            }

            var result = await response.Content.ReadFromJsonAsync<ShellOpenItemAdjustmentTransitionResultSummary>(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>.Failure(
                    result?.Message ?? $"The adjustment transition returned HTTP {(int)response.StatusCode}.",
                    string.IsNullOrWhiteSpace(result?.OutcomeCode)
                        ? (string.IsNullOrWhiteSpace(result?.TransitionCode) ? null : result.TransitionCode)
                        : result.OutcomeCode);
            }

            return result is null
                ? WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>.Failure(
                    $"The {transition} transition succeeded but returned an empty payload.")
                : WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to {Transition} open item adjustment request for {OpenItemType} {OpenItemId}.", transition, openItemType, openItemId);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentTransitionResultSummary>.Failure(
                $"Unable to {transition} the adjustment request. Check API availability and business-session headers.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<T>> GetOptionalAsync<T>(
        string requestUri,
        string operationLabel,
        string openItemType,
        Guid openItemId,
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
                var error = await ReadErrorAsync(response, cancellationToken);
                return WebShellAuthenticatedApiResult<T>.Failure(error.Message, error.Code);
            }

            var payload = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
            return payload is null
                ? WebShellAuthenticatedApiResult<T>.Failure($"{operationLabel} succeeded but returned an empty payload.")
                : WebShellAuthenticatedApiResult<T>.Success(payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load {OperationLabel} for {OpenItemType} {OpenItemId}.", operationLabel, openItemType, openItemId);
            return WebShellAuthenticatedApiResult<T>.Failure(
                $"Unable to load {operationLabel}. Check API availability and business-session headers.");
        }
    }

    private static async Task<ShellApiErrorPayload> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new ShellApiErrorPayload(null, WebShellBusinessSessionClient.AuthenticationRequiredError);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ShellApiErrorPayload("not_found", "The requested open item resource was not found in the active company context.");
        }

        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            string? code = null;
            if (payload?["code"]?.GetValue<string>() is { Length: > 0 } payloadCode)
            {
                code = payloadCode;
            }
            else if (payload?["outcomeCode"]?.GetValue<string>() is { Length: > 0 } outcomeCode)
            {
                code = outcomeCode;
            }
            else if (payload?["transitionCode"]?.GetValue<string>() is { Length: > 0 } transitionCode)
            {
                code = transitionCode;
            }

            if (payload?["message"]?.GetValue<string>() is { Length: > 0 } message)
            {
                return new ShellApiErrorPayload(code, message);
            }

            if (payload?["error"]?.GetValue<string>() is { Length: > 0 } error)
            {
                return new ShellApiErrorPayload(code, error);
            }
        }
        catch
        {
        }

        return new ShellApiErrorPayload(null, $"Open item request returned HTTP {(int)response.StatusCode}.");
    }

    private sealed record class ShellApiErrorPayload(string? Code, string Message);

    private static bool TryBuildAdjustmentPath(string? openItemType, Guid openItemId, string leaf, out string path)
    {
        path = openItemType?.Trim().ToLowerInvariant() switch
        {
            "ar" => $"accounting/open-items/ar/{openItemId:D}/{leaf}",
            "ap" => $"accounting/open-items/ap/{openItemId:D}/{leaf}",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryBuildAdjustmentRequestPath(
        string? openItemType,
        Guid openItemId,
        Guid requestId,
        string transition,
        out string path)
    {
        path = openItemType?.Trim().ToLowerInvariant() switch
        {
            "ar" => $"accounting/open-items/ar/{openItemId:D}/adjustment-request/{requestId:D}/{transition}",
            "ap" => $"accounting/open-items/ap/{openItemId:D}/adjustment-request/{requestId:D}/{transition}",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(path);
    }
}
