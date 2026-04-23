using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Web.Business.GL.JournalEntry;

public sealed class JournalEntrySourceDocumentTraceClient(
    HttpClient httpClient,
    ILogger<JournalEntrySourceDocumentTraceClient> logger)
{
    public async Task<JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>> GetAsync(
        Guid companyId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        var normalizedSourceType = NormalizeSourceType(sourceType);
        if (string.IsNullOrWhiteSpace(normalizedSourceType) || sourceId == Guid.Empty)
        {
            return JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.Failure(
                "Source document trace is not available for this journal entry source.",
                "not_found");
        }

        try
        {
            if (string.Equals(normalizedSourceType, "fx_revaluation", StringComparison.OrdinalIgnoreCase))
            {
                return await GetFxRevaluationTraceAsync(companyId, sourceId, cancellationToken);
            }

            var response = await httpClient.GetAsync(
                $"accounting/document-review/{normalizedSourceType}/{sourceId:D}?companyId={companyId:D}",
                cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.RequiresAuthentication();
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.NotFound(
                    "Source document summary was not found in the active company context.",
                    "not_found");
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                logger.LogWarning(
                    "Source document trace request failed for {SourceType} {SourceId}: {StatusCode} {Error}",
                    normalizedSourceType,
                    sourceId,
                    response.StatusCode,
                    error.Message);
                return JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.Failure(error.Message, error.Code);
            }

            var summary = await response.Content.ReadFromJsonAsync<JournalEntrySourceDocumentTraceSummary>(
                cancellationToken: cancellationToken);

            return summary is null
                ? JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.Failure(
                    "Source document trace returned an empty response.")
                : JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.Success(summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Unable to load source document trace for {SourceType} {SourceId}.",
                normalizedSourceType,
                sourceId);
            return JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.Failure(
                "Unable to load source document trace right now.");
        }
    }

    private static string? NormalizeSourceType(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return null;
        }

        return sourceType.Trim().ToLowerInvariant() switch
        {
            "manual_journal" => "manual_journal",
            "invoice" => "invoice",
            "credit_note" => "credit_note",
            "bill" => "bill",
            "vendor_credit" => "vendor_credit",
            "receive_payment" => "receive_payment",
            "credit_application" => "credit_application",
            "pay_bill" => "pay_bill",
            "vendor_credit_application" => "vendor_credit_application",
            "fx_revaluation" => "fx_revaluation",
            _ => null
        };
    }

    private async Task<JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>> GetFxRevaluationTraceAsync(
        Guid companyId,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync(
            $"accounting/fx-revaluation-batches/{sourceId:D}?companyId={companyId:D}",
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.RequiresAuthentication();
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.NotFound(
                "FX revaluation source batch was not found in the active company context.",
                "not_found");
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            logger.LogWarning(
                "FX revaluation source trace request failed for {SourceId}: {StatusCode} {Error}",
                sourceId,
                response.StatusCode,
                error.Message);
            return JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.Failure(error.Message, error.Code);
        }

        var detail = await response.Content.ReadFromJsonAsync<JournalEntryFxRevaluationBatchDetail>(
            cancellationToken: cancellationToken);

        return detail is null
            ? JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.Failure(
                "FX revaluation source trace returned an empty response.")
            : JournalEntryFxRevaluationApiResult<JournalEntrySourceDocumentTraceSummary>.Success(
                MapFxRevaluationTrace(detail));
    }

    private static JournalEntrySourceDocumentTraceSummary MapFxRevaluationTrace(
        JournalEntryFxRevaluationBatchDetail detail)
    {
        var unrealizedTotalBase = detail.Lines.Sum(static line => line.UnrealizedAmountBase);

        return new JournalEntrySourceDocumentTraceSummary
        {
            SourceType = "fx_revaluation",
            SourceTypeLabel = "FX Revaluation",
            Id = detail.Id,
            EntityNumber = detail.EntityNumber,
            DisplayNumber = detail.DisplayNumber,
            Status = detail.Status,
            DocumentDate = detail.DocumentDate,
            DueDate = null,
            CounterpartyLabel = "Open-item party",
            CounterpartyId = null,
            ControlAccountLabel = "Revaluation control",
            ControlAccountId = null,
            JournalEntryId = null,
            JournalEntryDisplayNumber = null,
            JournalEntryStatus = null,
            LifecycleMode = detail.BatchKind,
            LifecycleReason = BuildFxLifecycleReason(detail),
            TransactionCurrencyCode = detail.TransactionCurrencyCode,
            BaseCurrencyCode = detail.BaseCurrencyCode,
            TotalAmount = unrealizedTotalBase,
            Memo = detail.Memo,
            Lines = detail.Lines
                .Select(static line => new JournalEntrySourceDocumentTraceLineSummary
                {
                    LineNumber = line.LineNumber,
                    AccountCode = line.TargetOpenItemType,
                    AccountName = line.TargetBalanceSide,
                    Description = line.Description,
                    LineAmount = line.UnrealizedAmountBase,
                    TaxAmount = 0m,
                    TxDebit = line.OpenAmountTx,
                    TxCredit = null
                })
                .ToArray()
        };
    }

    private static string BuildFxLifecycleReason(JournalEntryFxRevaluationBatchDetail detail)
    {
        var kind = detail.BatchKind switch
        {
            "next_period_unwind" => "Next-period unwind batch",
            "revaluation" => "Period-end revaluation batch",
            _ => detail.BatchKind.Replace('_', ' ')
        };

        return $"{kind}; {detail.Lines.Count} line(s); FX {detail.FxRate:0.######} {detail.FxRateType}/{detail.FxRateUseCase}.";
    }

    private static async Task<JournalEntrySourceDocumentTraceErrorPayload> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
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
                return new JournalEntrySourceDocumentTraceErrorPayload(code, message);
            }

            if (payload?["error"]?.GetValue<string>() is { Length: > 0 } error)
            {
                return new JournalEntrySourceDocumentTraceErrorPayload(code, error);
            }
        }
        catch
        {
        }

        return new JournalEntrySourceDocumentTraceErrorPayload(
            null,
            $"Source document trace returned HTTP {(int)response.StatusCode}.");
    }
}

internal sealed record class JournalEntrySourceDocumentTraceErrorPayload(string? Code, string Message);

public sealed record class JournalEntrySourceDocumentTraceSummary
{
    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public string CounterpartyLabel { get; init; } = string.Empty;

    public Guid? CounterpartyId { get; init; }

    public string ControlAccountLabel { get; init; } = string.Empty;

    public Guid? ControlAccountId { get; init; }

    public Guid? JournalEntryId { get; init; }

    public string? JournalEntryDisplayNumber { get; init; }

    public string? JournalEntryStatus { get; init; }

    public string LifecycleMode { get; init; } = string.Empty;

    public string LifecycleReason { get; init; } = string.Empty;

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public decimal TotalAmount { get; init; }

    public string TotalCurrencyCode =>
        string.Equals(SourceType, "fx_revaluation", StringComparison.OrdinalIgnoreCase)
            ? BaseCurrencyCode
            : TransactionCurrencyCode;

    public string? Memo { get; init; }

    public IReadOnlyList<JournalEntrySourceDocumentTraceLineSummary> Lines { get; init; } =
        Array.Empty<JournalEntrySourceDocumentTraceLineSummary>();
}

public sealed record class JournalEntrySourceDocumentTraceLineSummary
{
    public int LineNumber { get; init; }

    public string AccountCode { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public decimal LineAmount { get; init; }

    public decimal TaxAmount { get; init; }

    public decimal? TxDebit { get; init; }

    public decimal? TxCredit { get; init; }
}
