using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Web.Business.GL.JournalEntry;

public sealed class JournalEntryFxRevaluationClient(HttpClient httpClient, ILogger<JournalEntryFxRevaluationClient> logger)
{
    public Task<JournalEntryFxRevaluationApiResult<IReadOnlyList<JournalEntryFxRevaluationBatchListItem>>> ListRecentAsync(
        Guid companyId,
        int take = 50,
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<JournalEntryFxRevaluationBatchListItem>>(
            $"accounting/fx-revaluation-batches?companyId={companyId:D}&take={take}",
            "FX revaluation batches",
            cancellationToken);

    public Task<JournalEntryFxRevaluationApiResult<JournalEntryFxRevaluationBatchDetail>> GetAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        GetAsync<JournalEntryFxRevaluationBatchDetail>(
            $"accounting/fx-revaluation-batches/{documentId:D}?companyId={companyId:D}",
            "FX revaluation batch",
            cancellationToken);

    public Task<JournalEntryFxRevaluationApiResult<JournalEntryFxRevaluationCascadePlan>> GetCascadeUnwindPlanAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        GetAsync<JournalEntryFxRevaluationCascadePlan>(
            $"accounting/fx-revaluation-batches/{documentId:D}/cascade-unwind-plan?companyId={companyId:D}",
            "FX revaluation cascade unwind plan",
            cancellationToken);

    public Task<JournalEntryFxRevaluationApiResult<JournalEntryFxRevaluationPrepareResult>> PrepareAsync(
        Guid companyId,
        Guid userId,
        DateOnly revaluationDate,
        string transactionCurrencyCode,
        bool includeAccountsReceivable,
        bool includeAccountsPayable,
        string? memo,
        Guid? bookId = null,
        Guid? acceptedFxSnapshotId = null,
        CancellationToken cancellationToken = default) =>
        PostAsync<JournalEntryFxRevaluationPrepareResult>(
            "accounting/fx-revaluation-batches/prepare",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                BookId = bookId,
                RevaluationDate = revaluationDate,
                TransactionCurrencyCode = transactionCurrencyCode,
                AcceptedFxSnapshotId = acceptedFxSnapshotId,
                IncludeAccountsReceivable = includeAccountsReceivable,
                IncludeAccountsPayable = includeAccountsPayable,
                Memo = memo
            },
            "FX revaluation preparation",
            cancellationToken);

    public Task<JournalEntryFxRevaluationApiResult<JournalEntryFxRevaluationUnwindPrepareResult>> PrepareNextPeriodUnwindAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        DateOnly unwindDate,
        string? memo,
        CancellationToken cancellationToken = default) =>
        PostAsync<JournalEntryFxRevaluationUnwindPrepareResult>(
            $"accounting/fx-revaluation-batches/{documentId:D}/prepare-next-period-unwind",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                UnwindDate = unwindDate,
                Memo = memo
            },
            "FX revaluation unwind preparation",
            cancellationToken);

    public Task<JournalEntryFxRevaluationApiResult<JournalEntryFxRevaluationCascadePrepareResult>> PrepareCascadeUnwindAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        DateOnly unwindDate,
        string? memo,
        CancellationToken cancellationToken = default) =>
        PostAsync<JournalEntryFxRevaluationCascadePrepareResult>(
            $"accounting/fx-revaluation-batches/{documentId:D}/prepare-cascade-unwind",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                UnwindDate = unwindDate,
                Memo = memo
            },
            "FX revaluation cascade unwind preparation",
            cancellationToken);

    public Task<JournalEntryFxRevaluationApiResult<JournalEntryFxRevaluationCascadePostResult>> AutoPostCascadeUnwindAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        DateOnly unwindDate,
        string? memo,
        CancellationToken cancellationToken = default) =>
        PostAsync<JournalEntryFxRevaluationCascadePostResult>(
            $"accounting/fx-revaluation-batches/{documentId:D}/auto-post-cascade-unwind",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                UnwindDate = unwindDate,
                Memo = memo,
                IdempotencyKey = $"fx-revaluation-cascade-unwind:{documentId:D}:{unwindDate:yyyyMMdd}"
            },
            "FX revaluation cascade unwind posting",
            cancellationToken);

    public Task<JournalEntryFxRevaluationApiResult<JournalEntryFxRevaluationPostResult>> PostAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        Guid? acceptedFxSnapshotId = null,
        CancellationToken cancellationToken = default) =>
        PostAsync<JournalEntryFxRevaluationPostResult>(
            $"accounting/fx-revaluation-batches/{documentId:D}/post",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                AcceptedFxSnapshotId = acceptedFxSnapshotId,
                IdempotencyKey = $"fx-revaluation:{documentId:D}"
            },
            "FX revaluation posting",
            cancellationToken);

    private async Task<JournalEntryFxRevaluationApiResult<T>> GetAsync<T>(
        string requestUri,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return JournalEntryFxRevaluationApiResult<T>.RequiresAuthentication();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return JournalEntryFxRevaluationApiResult<T>.NotFound($"{operationName} was not found.");
            }

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
                return result is null
                    ? JournalEntryFxRevaluationApiResult<T>.Failure($"{operationName} returned an empty response.")
                    : JournalEntryFxRevaluationApiResult<T>.Success(result);
            }

            var error = await ReadErrorMessageAsync(response, cancellationToken);
            return JournalEntryFxRevaluationApiResult<T>.Failure(error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load {OperationName} using request {RequestUri}.", operationName, requestUri);
            return JournalEntryFxRevaluationApiResult<T>.Failure($"{operationName} could not be loaded.");
        }
    }

    private async Task<JournalEntryFxRevaluationApiResult<T>> PostAsync<T>(
        string requestUri,
        object payload,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return JournalEntryFxRevaluationApiResult<T>.RequiresAuthentication();
            }

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
                return result is null
                    ? JournalEntryFxRevaluationApiResult<T>.Failure($"{operationName} succeeded but no response payload was returned.")
                    : JournalEntryFxRevaluationApiResult<T>.Success(result);
            }

            var error = await ReadErrorMessageAsync(response, cancellationToken);
            return JournalEntryFxRevaluationApiResult<T>.Failure(error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to complete {OperationName} using request {RequestUri}.", operationName, requestUri);
            return JournalEntryFxRevaluationApiResult<T>.Failure($"{operationName} could not be completed.");
        }
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JournalEntryFxRevaluationErrorPayload>(cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload?.Message))
            {
                return payload.Message;
            }
        }
        catch
        {
        }

        return $"FX revaluation request failed with HTTP {(int)response.StatusCode}.";
    }
}

public sealed record class JournalEntryFxRevaluationApiResult<T>
{
    public T? Value { get; init; }

    public bool RequiresSignIn { get; init; }

    public bool IsNotFound { get; init; }

    public string? ErrorMessage { get; init; }

    public static JournalEntryFxRevaluationApiResult<T> Success(T? value) =>
        new() { Value = value };

    public static JournalEntryFxRevaluationApiResult<T> RequiresAuthentication() =>
        new() { RequiresSignIn = true, ErrorMessage = "Your business session has expired. Please sign in again." };

    public static JournalEntryFxRevaluationApiResult<T> NotFound(string errorMessage) =>
        new() { IsNotFound = true, ErrorMessage = errorMessage };

    public static JournalEntryFxRevaluationApiResult<T> Failure(string errorMessage) =>
        new() { ErrorMessage = errorMessage };
}

public sealed record class JournalEntryFxRevaluationPrepareResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    Guid? BookId,
    string? BookCode,
    string? AccountingStandard,
    string? RevaluationProfile,
    string? FxRoundingPolicy,
    int PreparedLineCount,
    string Status);

public sealed record class JournalEntryFxRevaluationPostResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt,
    IReadOnlyList<string> Warnings);

public sealed record class JournalEntryFxRevaluationBatchListItem(
    Guid Id,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    string BatchKind,
    Guid? ReversalOfDocumentId,
    Guid? BookId,
    string? BookCode,
    string? AccountingStandard,
    string? RevaluationProfile,
    string? FxRoundingPolicy,
    DateOnly DocumentDate,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    Guid? FxSnapshotId,
    decimal FxRate,
    int LineCount,
    decimal UnrealizedTotalBase,
    Guid? LinkedJournalEntryId,
    string? LinkedJournalEntryDisplayNumber,
    DateTimeOffset? LinkedJournalPostedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record class JournalEntryFxRevaluationBatchDetail(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string DisplayNumber,
    string Status,
    string BatchKind,
    Guid? ReversalOfDocumentId,
    Guid? BookId,
    string? BookCode,
    string? AccountingStandard,
    string? RevaluationProfile,
    string? FxRoundingPolicy,
    DateOnly DocumentDate,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    Guid? FxSnapshotId,
    decimal FxRate,
    string FxRateType,
    string FxQuoteBasis,
    string FxRateUseCase,
    string FxPostingReason,
    DateOnly FxRequestedDate,
    DateOnly FxEffectiveDate,
    string FxSource,
    Guid UnrealizedFxGainAccountId,
    Guid UnrealizedFxLossAccountId,
    string? Memo,
    IReadOnlyList<JournalEntryFxRevaluationBatchLine> Lines);

public sealed record class JournalEntryFxRevaluationBatchLine(
    int LineNumber,
    string TargetOpenItemType,
    Guid TargetOpenItemId,
    string TargetBalanceSide,
    Guid TargetControlAccountId,
    Guid OffsetAccountId,
    Guid PartyId,
    string Description,
    decimal OpenAmountTx,
    decimal CarryingAmountBase,
    decimal RevaluedAmountBase,
    decimal UnrealizedAmountBase);

public sealed record class JournalEntryFxRevaluationUnwindPrepareResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    int PreparedLineCount,
    string Status);

public sealed record class JournalEntryFxRevaluationCascadePlan(
    Guid RequestedDocumentId,
    string RequestedDisplayNumber,
    Guid NextDocumentId,
    string NextDisplayNumber,
    bool RequestedBatchIsTail,
    int ActiveRevaluationCount,
    IReadOnlyList<JournalEntryFxRevaluationCascadePlanStep> ActiveRevaluationChain);

public sealed record class JournalEntryFxRevaluationCascadePlanStep(
    Guid DocumentId,
    string DisplayNumber,
    DateOnly RevaluationDate,
    DateTimeOffset PostedAt,
    bool IsRequestedBatch,
    bool IsNextStep);

public sealed record class JournalEntryFxRevaluationCascadePrepareResult(
    Guid RequestedDocumentId,
    string RequestedDisplayNumber,
    Guid TargetDocumentId,
    string TargetDisplayNumber,
    bool RequestedBatchIsTail,
    int ActiveRevaluationCount,
    Guid DraftDocumentId,
    string DraftEntityNumber,
    string DraftDisplayNumber,
    int PreparedLineCount,
    string Status);

public sealed record class JournalEntryFxRevaluationCascadePostResult(
    Guid RequestedDocumentId,
    string RequestedDisplayNumber,
    bool RequestedBatchWasTail,
    int PostedStepCount,
    IReadOnlyList<JournalEntryFxRevaluationCascadePostStep> PostedSteps);

public sealed record class JournalEntryFxRevaluationCascadePostStep(
    Guid SourceDocumentId,
    string SourceDisplayNumber,
    Guid UnwindDocumentId,
    string UnwindDisplayNumber,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    DateTimeOffset PostedAt,
    IReadOnlyList<string> Warnings);

internal sealed record class JournalEntryFxRevaluationErrorPayload(string? Message);
