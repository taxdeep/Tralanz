using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresReceiptGrIrApSettlementControlStore : IReceiptGrIrApSettlementControlStore
{
    private const string SettlementLinesTableName = "receipt_grir_ap_settlement_lines";
    private const string SettlementBatchesTableName = "receipt_grir_ap_settlement_batches";
    private const string SettlementBatchLinesTableName = "receipt_grir_ap_settlement_batch_lines";
    private const string PurchaseVarianceLinesTableName = "receipt_grir_ap_purchase_variance_lines";
    private const string BridgeLinesTableName = "receipt_grir_bridge_lines";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresReceiptGrIrApSettlementControlStore(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<ReceiptGrIrApSettlementSummary> RefreshReceiptSettlementControlAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (!await CanBuildSettlementLaneAsync(scope, cancellationToken))
        {
            return BuildEmptyReceiptSummary(receiptDocumentId);
        }

        await EnsureSchemaAsync(scope, cancellationToken);
        await AcquireSettlementLockAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await UpsertReceiptSettlementLinesAsync(scope, companyId.Value, userId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementJournalStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementOpenItemClearingStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await UpsertReceiptSettlementPurchaseVarianceLinesAsync(scope, companyId.Value, userId.Value, receiptDocumentId, cancellationToken);

        return await LoadReceiptSettlementSummaryAsync(scope, companyId.Value, receiptDocumentId, cancellationToken)
            ?? BuildEmptyReceiptSummary(receiptDocumentId);
    }

    public async Task<ReceiptGrIrApSettlementSummary> RefreshReceiptSettlementJournalReconciliationAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (!await CanBuildSettlementLaneAsync(scope, cancellationToken))
        {
            return BuildEmptyReceiptSummary(receiptDocumentId);
        }

        await EnsureSchemaAsync(scope, cancellationToken);
        await AcquireSettlementLockAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementJournalStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementOpenItemClearingStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await UpsertReceiptSettlementPurchaseVarianceLinesAsync(scope, companyId.Value, userId.Value, receiptDocumentId, cancellationToken);

        return await LoadReceiptSettlementSummaryAsync(scope, companyId.Value, receiptDocumentId, cancellationToken)
            ?? BuildEmptyReceiptSummary(receiptDocumentId);
    }

    public async Task<ReceiptGrIrApSettlementSummary> RefreshReceiptSettlementVarianceControlAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (!await CanBuildSettlementLaneAsync(scope, cancellationToken))
        {
            return BuildEmptyReceiptSummary(receiptDocumentId);
        }

        await EnsureSchemaAsync(scope, cancellationToken);
        await AcquireSettlementLockAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementJournalStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementOpenItemClearingStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await UpsertReceiptSettlementPurchaseVarianceLinesAsync(scope, companyId.Value, userId.Value, receiptDocumentId, cancellationToken);

        return await LoadReceiptSettlementSummaryAsync(scope, companyId.Value, receiptDocumentId, cancellationToken)
            ?? BuildEmptyReceiptSummary(receiptDocumentId);
    }

    public async Task<ReceiptGrIrApSettlementSummary?> GetReceiptSettlementSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (!await TableExistsAsync(scope, SettlementLinesTableName, cancellationToken))
        {
            return BuildEmptyReceiptSummary(receiptDocumentId);
        }

        if (!await EnsureSettlementBatchSchemaForReadAsync(scope, cancellationToken))
        {
            return BuildEmptyReceiptSummary(receiptDocumentId);
        }

        return await LoadReceiptSettlementSummaryAsync(scope, companyId.Value, receiptDocumentId, cancellationToken)
            ?? BuildEmptyReceiptSummary(receiptDocumentId);
    }

    public async Task<IReadOnlyDictionary<Guid, ReceiptGrIrApSettlementSummary>> GetReceiptSettlementSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentIds.Count == 0)
        {
            return new Dictionary<Guid, ReceiptGrIrApSettlementSummary>();
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var distinctIds = receiptDocumentIds.Distinct().ToArray();
        if (!await TableExistsAsync(scope, SettlementLinesTableName, cancellationToken))
        {
            return distinctIds.ToDictionary(
                static id => id,
                static id => BuildEmptyReceiptSummary(id));
        }

        if (!await EnsureSettlementBatchSchemaForReadAsync(scope, cancellationToken))
        {
            return distinctIds.ToDictionary(
                static id => id,
                static id => BuildEmptyReceiptSummary(id));
        }

        return await LoadReceiptSettlementSummariesAsync(scope, companyId.Value, distinctIds, cancellationToken);
    }

    public async Task<IReadOnlyList<ReceiptGrIrApSettlementBatchSummary>> ListReceiptSettlementBatchesAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (!await EnsureSettlementBatchSchemaForReadAsync(scope, cancellationToken))
        {
            return Array.Empty<ReceiptGrIrApSettlementBatchSummary>();
        }

        return await LoadReceiptSettlementBatchSummariesAsync(
            scope,
            companyId.Value,
            receiptDocumentId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ReceiptGrIrApPurchaseVarianceLineSummary>> ListReceiptPurchaseVarianceLinesAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (!await EnsureSettlementBatchSchemaForReadAsync(scope, cancellationToken))
        {
            return Array.Empty<ReceiptGrIrApPurchaseVarianceLineSummary>();
        }

        return await LoadReceiptPurchaseVarianceLineSummariesAsync(
            scope,
            companyId.Value,
            receiptDocumentId,
            cancellationToken);
    }

    public async Task<BillGrIrApSettlementSummary?> GetBillSettlementSummaryAsync(
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (!await TableExistsAsync(scope, SettlementLinesTableName, cancellationToken))
        {
            return BuildEmptyBillSummary(billDocumentId);
        }

        if (!await EnsureSettlementBatchSchemaForReadAsync(scope, cancellationToken))
        {
            return BuildEmptyBillSummary(billDocumentId);
        }

        return await LoadBillSettlementSummaryAsync(scope, companyId.Value, billDocumentId, cancellationToken)
            ?? BuildEmptyBillSummary(billDocumentId);
    }

    public async Task<ReceiptGrIrApSettlementExecutionResult> ExecuteReceiptSettlementAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        ReceiptGrIrApSettlementExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        var requestedAmountBase = request.SettlementAmountBase.HasValue
            ? Round6(request.SettlementAmountBase.Value)
            : (decimal?)null;
        if (requestedAmountBase <= 0)
        {
            throw new InvalidOperationException("GR/IR settlement amount must be greater than zero.");
        }

        var idempotencyKey = BuildSettlementIdempotencyKey(
            companyId.Value,
            receiptDocumentId,
            requestedAmountBase,
            request.IdempotencyKey);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (!await CanBuildSettlementLaneAsync(scope, cancellationToken))
        {
            throw new InvalidOperationException("The GR/IR settlement lane is not ready. Refresh GR/IR bridge, post GR/IR, and ensure AP open item truth exists before settlement execution.");
        }

        await EnsureSchemaAsync(scope, cancellationToken);
        await AcquireSettlementLockAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await UpsertReceiptSettlementLinesAsync(scope, companyId.Value, userId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementJournalStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementOpenItemClearingStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);

        var existing = await TryLoadSettlementExecutionResultAsync(
            scope,
            companyId.Value,
            receiptDocumentId,
            idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var executableLines = await LoadExecutableSettlementLinesAsync(
            scope,
            companyId.Value,
            receiptDocumentId,
            cancellationToken);
        if (executableLines.Count == 0)
        {
            throw new InvalidOperationException("No eligible GR/IR settlement slice remains. Refresh settlement control truth and resolve blocked lines before execution.");
        }

        var totalRemainingAmountBase = Round6(executableLines.Sum(static line => line.RemainingAmountBase));
        var amountToSettleBase = requestedAmountBase ?? totalRemainingAmountBase;
        if (amountToSettleBase > totalRemainingAmountBase)
        {
            throw new InvalidOperationException(
                $"GR/IR settlement amount {amountToSettleBase:N6} exceeds remaining eligible amount {totalRemainingAmountBase:N6}.");
        }

        var allocations = BuildSettlementAllocations(executableLines, amountToSettleBase);
        var batchId = Guid.NewGuid();
        await CreateSettlementBatchAsync(
            scope,
            companyId.Value,
            userId.Value,
            receiptDocumentId,
            batchId,
            idempotencyKey,
            amountToSettleBase,
            allocations,
            cancellationToken);
        await InsertSettlementBatchLinesAsync(scope, companyId.Value, batchId, allocations, cancellationToken);
        await ApplySettlementAllocationsAsync(scope, companyId.Value, allocations, cancellationToken);
        await UpsertReceiptSettlementLinesAsync(scope, companyId.Value, userId.Value, receiptDocumentId, cancellationToken);

        var summary = await LoadReceiptSettlementSummaryAsync(scope, companyId.Value, receiptDocumentId, cancellationToken)
            ?? BuildEmptyReceiptSummary(receiptDocumentId);

        return new ReceiptGrIrApSettlementExecutionResult(
            receiptDocumentId,
            batchId,
            idempotencyKey,
            amountToSettleBase,
            Round6(allocations.Sum(static allocation => allocation.SettledQuantity)),
            Round6(allocations.Sum(static allocation => allocation.SettledAmountBase)),
            allocations.Count,
            summary);
    }

    public async Task<ReceiptGrIrApOpenItemClearingResult> ClearReceiptSettlementOpenItemsAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        if (settlementBatchId == Guid.Empty)
        {
            throw new ArgumentException("Settlement batch id is required.", nameof(settlementBatchId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (!await CanBuildOpenItemClearingLaneAsync(scope, cancellationToken))
        {
            throw new InvalidOperationException("The GR/IR AP open-item clearing lane is not ready. Settlement applications, posted settlement journals, and AP open-item truth are required before clearing.");
        }

        await EnsureSchemaAsync(scope, cancellationToken);
        await AcquireSettlementLockAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementJournalStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementOpenItemClearingStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);

        var existing = await TryLoadExistingOpenItemClearingResultAsync(
            scope,
            companyId.Value,
            receiptDocumentId,
            settlementBatchId,
            cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var header = await LoadOpenItemClearingBatchHeaderAsync(
            scope,
            companyId.Value,
            receiptDocumentId,
            settlementBatchId,
            cancellationToken);
        if (header is null)
        {
            throw new InvalidOperationException("GR/IR settlement batch was not found for the receipt.");
        }

        if (header.BatchStatus != "posted")
        {
            throw new InvalidOperationException("Only executed GR/IR settlement batches can clear AP open items.");
        }

        if (header.JournalStatus != ReceiptGrIrApSettlementJournalStatusPolicy.Posted)
        {
            throw new InvalidOperationException(
                $"GR/IR settlement batch journal status '{header.JournalStatus}' is not eligible for AP open-item clearing.");
        }

        if (header.OpenItemClearingStatus != ReceiptGrIrApOpenItemClearingStatusPolicy.NotCleared)
        {
            throw new InvalidOperationException(
                $"GR/IR settlement batch AP open-item clearing status '{header.OpenItemClearingStatus}' is not eligible for clearing.");
        }

        var allocations = await LoadOpenItemClearingAllocationsAsync(
            scope,
            companyId.Value,
            settlementBatchId,
            cancellationToken);
        if (allocations.Count == 0)
        {
            throw new InvalidOperationException("GR/IR settlement batch has no AP open-item slice to clear.");
        }

        foreach (var allocation in allocations)
        {
            await ApplyOpenItemClearingAllocationAsync(
                scope,
                companyId.Value,
                userId.Value,
                settlementBatchId,
                allocation,
                cancellationToken);
        }

        await MarkOpenItemClearingBatchClearedAsync(
            scope,
            companyId.Value,
            userId.Value,
            settlementBatchId,
            cancellationToken);
        await RefreshReceiptSettlementOpenItemClearingStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await UpsertReceiptSettlementPurchaseVarianceLinesAsync(scope, companyId.Value, userId.Value, receiptDocumentId, cancellationToken);

        return await LoadOpenItemClearingResultAsync(
            scope,
            companyId.Value,
            receiptDocumentId,
            settlementBatchId,
            cancellationToken);
    }

    public async Task<ReceiptGrIrApOpenItemClearingReversalResult> ReverseReceiptSettlementOpenItemClearingAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        if (settlementBatchId == Guid.Empty)
        {
            throw new ArgumentException("Settlement batch id is required.", nameof(settlementBatchId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (!await CanBuildOpenItemClearingLaneAsync(scope, cancellationToken))
        {
            throw new InvalidOperationException("The GR/IR AP open-item clearing lane is not ready. Settlement applications, posted settlement journals, and AP open-item truth are required before reversal.");
        }

        await EnsureSchemaAsync(scope, cancellationToken);
        await AcquireSettlementLockAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementJournalStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);
        await RefreshReceiptSettlementOpenItemClearingStatusesAsync(scope, companyId.Value, receiptDocumentId, cancellationToken);

        var existing = await TryLoadExistingOpenItemClearingReversalResultAsync(
            scope,
            companyId.Value,
            receiptDocumentId,
            settlementBatchId,
            cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var header = await LoadOpenItemClearingBatchHeaderAsync(
            scope,
            companyId.Value,
            receiptDocumentId,
            settlementBatchId,
            cancellationToken);
        if (header is null)
        {
            throw new InvalidOperationException("GR/IR settlement batch was not found for the receipt.");
        }

        if (header.OpenItemClearingStatus is not (
            ReceiptGrIrApOpenItemClearingStatusPolicy.ClearingStale or
            ReceiptGrIrApOpenItemClearingStatusPolicy.ClearingInconsistent))
        {
            throw new InvalidOperationException(
                $"GR/IR settlement batch AP open-item clearing status '{header.OpenItemClearingStatus}' is not eligible for reversal.");
        }

        if (header.JournalStatus == ReceiptGrIrApSettlementJournalStatusPolicy.Posted)
        {
            throw new InvalidOperationException("Posted GR/IR settlement journals must not have AP open-item clearing reversed.");
        }

        var applications = await LoadOpenItemClearingApplicationsAsync(
            scope,
            companyId.Value,
            settlementBatchId,
            cancellationToken);
        if (applications.Count == 0)
        {
            throw new InvalidOperationException("GR/IR settlement batch has no AP open-item clearing applications to reverse.");
        }

        foreach (var application in applications)
        {
            await ReverseOpenItemClearingApplicationAsync(
                scope,
                companyId.Value,
                application,
                cancellationToken);
        }

        await DeleteOpenItemClearingApplicationsAsync(
            scope,
            companyId.Value,
            settlementBatchId,
            cancellationToken);
        await MarkOpenItemClearingBatchReversedAsync(
            scope,
            companyId.Value,
            userId.Value,
            settlementBatchId,
            applications.Count,
            Round6(applications.Sum(static application => application.AppliedAmountTx)),
            Round6(applications.Sum(static application => application.AppliedAmountBase)),
            cancellationToken);
        await UpsertReceiptSettlementPurchaseVarianceLinesAsync(scope, companyId.Value, userId.Value, receiptDocumentId, cancellationToken);

        return await LoadOpenItemClearingReversalResultAsync(
            scope,
            companyId.Value,
            receiptDocumentId,
            settlementBatchId,
            cancellationToken);
    }

    private static async Task<bool> CanBuildSettlementLaneAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken) =>
        await TableExistsAsync(scope, BridgeLinesTableName, cancellationToken) &&
        await TableExistsAsync(scope, "bills", cancellationToken) &&
        await TableExistsAsync(scope, "ap_open_items", cancellationToken) &&
        await TableExistsAsync(scope, "journal_entries", cancellationToken);

    private static async Task<bool> CanBuildOpenItemClearingLaneAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken) =>
        await CanBuildSettlementLaneAsync(scope, cancellationToken) &&
        await TableExistsAsync(scope, "settlement_applications", cancellationToken);

    private static async Task<bool> EnsureSettlementBatchSchemaForReadAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        if (!await CanBuildSettlementLaneAsync(scope, cancellationToken))
        {
            return false;
        }

        await EnsureSchemaAsync(scope, cancellationToken);
        return await TableExistsAsync(scope, SettlementBatchesTableName, cancellationToken) &&
            await TableExistsAsync(scope, SettlementBatchLinesTableName, cancellationToken) &&
            await TableExistsAsync(scope, PurchaseVarianceLinesTableName, cancellationToken);
    }

    private static string BuildSettlementIdempotencyKey(
        Guid companyId,
        Guid receiptDocumentId,
        decimal? requestedAmountBase,
        string? explicitKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey.Trim();
        }

        var amountKey = requestedAmountBase.HasValue
            ? requestedAmountBase.Value.ToString("0.000000", CultureInfo.InvariantCulture)
            : "remaining";
        return $"receipt-grir-ap-settlement:{companyId:N}:{receiptDocumentId:N}:{amountKey}";
    }

    private static async Task AcquireSettlementLockAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand("select pg_advisory_xact_lock(hashtext(@lock_key));");
        command.Parameters.AddWithValue("lock_key", $"receipt-grir-ap-settlement:{companyId:N}:{receiptDocumentId:N}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RefreshReceiptSettlementJournalStatusesAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            update {SettlementBatchesTableName} batch
            set journal_status = case
                  when batch.journal_entry_id is null then '{ReceiptGrIrApSettlementJournalStatusPolicy.NotPosted}'
                  when je.id is null then '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalInconsistent}'
                  when je.source_type <> 'receipt_grir_ap_settlement_posting' or je.source_id <> batch.id then '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalInconsistent}'
                  when je.status = 'posted' then '{ReceiptGrIrApSettlementJournalStatusPolicy.Posted}'
                  else '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalStale}'
                end,
                journal_blocked_reason_code = case
                  when batch.journal_entry_id is null then null
                  when je.id is null then 'journal_missing'
                  when je.source_type <> 'receipt_grir_ap_settlement_posting' or je.source_id <> batch.id then 'journal_source_mismatch'
                  when je.status = 'posted' then null
                  else 'journal_not_posted'
                end,
                journal_refreshed_at = now()
            from {SettlementBatchesTableName} b
            left join journal_entries je
              on je.company_id = b.company_id
             and je.id = b.journal_entry_id
            where batch.company_id = b.company_id
              and batch.id = b.id
              and batch.company_id = @company_id
              and batch.receipt_id = @receipt_id
              and batch.status = 'posted';
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RefreshReceiptSettlementOpenItemClearingStatusesAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(scope, "settlement_applications", cancellationToken))
        {
            return;
        }

        await using var command = scope.CreateCommand(
            $"""
            with batch_truth as (
              select
                batch.id as settlement_batch_id,
                batch.journal_status,
                batch.settled_amount_base,
                coalesce(app.applied_amount_base, 0)::numeric(20,6) as applied_amount_base,
                bool_or(oi.id is null) as missing_ap_open_item,
                bool_or(oi.id is not null and oi.status not in ('open', 'partially_applied')) as ap_open_item_not_open,
                bool_or(oi.id is not null and oi.document_currency_code <> oi.base_currency_code) as fx_not_supported,
                bool_or(oi.id is not null and round(alloc.amount_base, 6) > round(oi.open_amount_base, 6)) as amount_exceeded
              from {SettlementBatchesTableName} batch
              left join lateral (
                select
                  batch_line.ap_open_item_id,
                  sum(batch_line.settled_amount_base)::numeric(20,6) as amount_base
                from {SettlementBatchLinesTableName} batch_line
                where batch_line.company_id = batch.company_id
                  and batch_line.settlement_batch_id = batch.id
                group by batch_line.ap_open_item_id
              ) alloc on true
              left join ap_open_items oi
                on oi.company_id = batch.company_id
               and oi.id = alloc.ap_open_item_id
              left join lateral (
                select sum(sa.applied_amount_base)::numeric(20,6) as applied_amount_base
                from settlement_applications sa
                where sa.company_id = batch.company_id
                  and sa.source_type = 'receipt_grir_ap_settlement'
                  and sa.source_id = batch.id
              ) app on true
              where batch.company_id = @company_id
                and batch.receipt_id = @receipt_id
                and batch.status = 'posted'
              group by batch.id, batch.journal_status, batch.settled_amount_base, app.applied_amount_base
            )
            update {SettlementBatchesTableName} batch
            set open_item_clearing_status = case
                  when batch.open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.Reversed}' then '{ReceiptGrIrApOpenItemClearingStatusPolicy.Reversed}'
                  when round(batch_truth.applied_amount_base, 6) = round(batch_truth.settled_amount_base, 6)
                    and batch_truth.applied_amount_base > 0
                    and batch_truth.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalStale}' then '{ReceiptGrIrApOpenItemClearingStatusPolicy.ClearingStale}'
                  when round(batch_truth.applied_amount_base, 6) = round(batch_truth.settled_amount_base, 6)
                    and batch_truth.applied_amount_base > 0
                    and batch_truth.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalInconsistent}' then '{ReceiptGrIrApOpenItemClearingStatusPolicy.ClearingInconsistent}'
                  when round(batch_truth.applied_amount_base, 6) = round(batch_truth.settled_amount_base, 6)
                    and batch_truth.applied_amount_base > 0 then '{ReceiptGrIrApOpenItemClearingStatusPolicy.Cleared}'
                  when batch_truth.applied_amount_base > 0 then '{ReceiptGrIrApOpenItemClearingStatusPolicy.ClearingInconsistent}'
                  when batch_truth.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.NotPosted}' then '{ReceiptGrIrApOpenItemClearingStatusPolicy.BlockedJournalNotPosted}'
                  when batch_truth.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalStale}' then '{ReceiptGrIrApOpenItemClearingStatusPolicy.BlockedJournalStale}'
                  when batch_truth.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalInconsistent}' then '{ReceiptGrIrApOpenItemClearingStatusPolicy.BlockedJournalInconsistent}'
                  when batch_truth.journal_status <> '{ReceiptGrIrApSettlementJournalStatusPolicy.Posted}' then '{ReceiptGrIrApOpenItemClearingStatusPolicy.BlockedJournalNotPosted}'
                  when batch_truth.missing_ap_open_item then '{ReceiptGrIrApOpenItemClearingStatusPolicy.BlockedMissingApOpenItem}'
                  when batch_truth.ap_open_item_not_open then '{ReceiptGrIrApOpenItemClearingStatusPolicy.BlockedOpenItemNotOpen}'
                  when batch_truth.fx_not_supported then '{ReceiptGrIrApOpenItemClearingStatusPolicy.BlockedFxNotSupported}'
                  when batch_truth.amount_exceeded then '{ReceiptGrIrApOpenItemClearingStatusPolicy.BlockedAmountExceeded}'
                  else '{ReceiptGrIrApOpenItemClearingStatusPolicy.NotCleared}'
                end,
                open_item_clearing_blocked_reason_code = case
                  when batch.open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.Reversed}' then null
                  when round(batch_truth.applied_amount_base, 6) = round(batch_truth.settled_amount_base, 6)
                    and batch_truth.applied_amount_base > 0
                    and batch_truth.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalStale}' then 'cleared_journal_stale'
                  when round(batch_truth.applied_amount_base, 6) = round(batch_truth.settled_amount_base, 6)
                    and batch_truth.applied_amount_base > 0
                    and batch_truth.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalInconsistent}' then 'cleared_journal_inconsistent'
                  when round(batch_truth.applied_amount_base, 6) = round(batch_truth.settled_amount_base, 6)
                    and batch_truth.applied_amount_base > 0 then null
                  when batch_truth.applied_amount_base > 0 then 'clearing_amount_mismatch'
                  when batch_truth.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.NotPosted}' then 'journal_not_posted'
                  when batch_truth.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalStale}' then 'journal_stale'
                  when batch_truth.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalInconsistent}' then 'journal_inconsistent'
                  when batch_truth.journal_status <> '{ReceiptGrIrApSettlementJournalStatusPolicy.Posted}' then 'journal_not_posted'
                  when batch_truth.missing_ap_open_item then 'missing_ap_open_item'
                  when batch_truth.ap_open_item_not_open then 'ap_open_item_not_open'
                  when batch_truth.fx_not_supported then 'fx_not_supported'
                  when batch_truth.amount_exceeded then 'ap_open_item_amount_exceeded'
                  else null
                end,
                open_item_clearing_refreshed_at = now()
            from batch_truth
            where batch.company_id = @company_id
              and batch.id = batch_truth.settlement_batch_id;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertReceiptSettlementPurchaseVarianceLinesAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(scope, PurchaseVarianceLinesTableName, cancellationToken))
        {
            return;
        }

        await using (var deleteCommand = scope.CreateCommand(
            $"""
            delete from {PurchaseVarianceLinesTableName}
            where company_id = @company_id
              and receipt_id = @receipt_id;
            """))
        {
            deleteCommand.Parameters.AddWithValue("company_id", companyId);
            deleteCommand.Parameters.AddWithValue("receipt_id", receiptDocumentId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = scope.CreateCommand(
            $"""
            with batch_truth as (
              select
                batch.company_id,
                settlement_line.receipt_id,
                settlement_line.receipt_line_number,
                batch.id as settlement_batch_id,
                batch_line.id as settlement_batch_line_id,
                settlement_line.id as settlement_line_id,
                settlement_line.bridge_line_id,
                settlement_line.bill_id,
                settlement_line.bill_line_number,
                settlement_line.item_id,
                settlement_line.warehouse_id,
                settlement_line.uom_code,
                batch.status as batch_status,
                batch.journal_status,
                batch.open_item_clearing_status,
                bill.status as bill_status,
                bill.fx_rate,
                bill_line.quantity as bill_line_quantity,
                bill_line.line_amount,
                batch_line.settled_quantity,
                batch_line.settled_amount_base as grir_amount_base
              from {SettlementBatchLinesTableName} batch_line
              join {SettlementBatchesTableName} batch
                on batch.company_id = batch_line.company_id
               and batch.id = batch_line.settlement_batch_id
              join {SettlementLinesTableName} settlement_line
                on settlement_line.company_id = batch_line.company_id
               and settlement_line.id = batch_line.settlement_line_id
              join bills bill
                on bill.company_id = settlement_line.company_id
               and bill.id = settlement_line.bill_id
              join bill_lines bill_line
                on bill_line.company_id = settlement_line.company_id
               and bill_line.bill_id = settlement_line.bill_id
               and bill_line.line_number = settlement_line.bill_line_number
              where batch.company_id = @company_id
                and settlement_line.receipt_id = @receipt_id
                and batch.status = 'posted'
            ),
            measured as (
              select
                *,
                case
                  when bill_line_quantity is null or round(bill_line_quantity, 6) <= 0 then null
                  else round(settled_quantity * round(line_amount * fx_rate, 6) / bill_line_quantity, 6)
                end as bill_amount_base
              from batch_truth
            ),
            classified as (
              select
                *,
                round(coalesce(bill_amount_base, 0) - grir_amount_base, 6) as variance_amount_base,
                case
                  when batch_status <> 'posted' then '{ReceiptGrIrApPurchaseVarianceStatusPolicy.BlockedSettlementNotPosted}'
                  when journal_status <> '{ReceiptGrIrApSettlementJournalStatusPolicy.Posted}' then '{ReceiptGrIrApPurchaseVarianceStatusPolicy.BlockedJournalNotPosted}'
                  when open_item_clearing_status <> '{ReceiptGrIrApOpenItemClearingStatusPolicy.Cleared}' then '{ReceiptGrIrApPurchaseVarianceStatusPolicy.BlockedOpenItemNotCleared}'
                  when bill_status <> 'posted' then '{ReceiptGrIrApPurchaseVarianceStatusPolicy.BlockedBillNotPosted}'
                  when bill_amount_base is null then '{ReceiptGrIrApPurchaseVarianceStatusPolicy.BlockedQuantityBasisMissing}'
                  when abs(round(bill_amount_base - grir_amount_base, 6)) = 0 then '{ReceiptGrIrApPurchaseVarianceStatusPolicy.NoVariance}'
                  else '{ReceiptGrIrApPurchaseVarianceStatusPolicy.RecognizedInSettlement}'
                end as variance_status,
                case
                  when batch_status <> 'posted' then 'settlement_batch_not_posted'
                  when journal_status <> '{ReceiptGrIrApSettlementJournalStatusPolicy.Posted}' then 'journal_not_posted_or_stale'
                  when open_item_clearing_status <> '{ReceiptGrIrApOpenItemClearingStatusPolicy.Cleared}' then 'open_item_not_cleared'
                  when bill_status <> 'posted' then 'bill_not_posted'
                  when bill_amount_base is null then 'bill_line_quantity_missing'
                  else null
                end as blocked_reason_code
              from measured
            )
            insert into {PurchaseVarianceLinesTableName} (
              id,
              company_id,
              receipt_id,
              receipt_line_number,
              settlement_batch_id,
              settlement_batch_line_id,
              settlement_line_id,
              bridge_line_id,
              bill_id,
              bill_line_number,
              item_id,
              warehouse_id,
              uom_code,
              settled_quantity,
              grir_amount_base,
              bill_amount_base,
              variance_amount_base,
              variance_status,
              blocked_reason_code,
              refreshed_by_user_id,
              refreshed_at
            )
            select
              gen_random_uuid(),
              company_id,
              receipt_id,
              receipt_line_number,
              settlement_batch_id,
              settlement_batch_line_id,
              settlement_line_id,
              bridge_line_id,
              bill_id,
              bill_line_number,
              item_id,
              warehouse_id,
              uom_code,
              settled_quantity,
              grir_amount_base,
              coalesce(bill_amount_base, 0)::numeric(20,6),
              variance_amount_base,
              variance_status,
              blocked_reason_code,
              @user_id,
              now()
            from classified
            on conflict (company_id, settlement_batch_line_id)
            do update set
              receipt_id = excluded.receipt_id,
              receipt_line_number = excluded.receipt_line_number,
              settlement_batch_id = excluded.settlement_batch_id,
              settlement_line_id = excluded.settlement_line_id,
              bridge_line_id = excluded.bridge_line_id,
              bill_id = excluded.bill_id,
              bill_line_number = excluded.bill_line_number,
              item_id = excluded.item_id,
              warehouse_id = excluded.warehouse_id,
              uom_code = excluded.uom_code,
              settled_quantity = excluded.settled_quantity,
              grir_amount_base = excluded.grir_amount_base,
              bill_amount_base = excluded.bill_amount_base,
              variance_amount_base = excluded.variance_amount_base,
              variance_status = excluded.variance_status,
              blocked_reason_code = excluded.blocked_reason_code,
              refreshed_by_user_id = excluded.refreshed_by_user_id,
              refreshed_at = excluded.refreshed_at;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertReceiptSettlementLinesAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            with bridge_truth as (
              select
                bridge.id as bridge_line_id,
                bridge.company_id,
                bridge.receipt_id,
                bridge.receipt_line_number,
                bridge.bill_id,
                bridge.bill_line_number,
                bridge.item_id,
                bridge.warehouse_id,
                bridge.uom_code,
                bridge.bridge_quantity,
                bridge.bridge_amount_base,
                bridge.bridge_status,
                bridge.journal_entry_id,
                bridge.journal_entry_display_number,
                b.status as bill_status,
                oi.id as ap_open_item_id,
                je.status as journal_status,
                coalesce(existing.settled_quantity, 0)::numeric(20,6) as settled_quantity,
                coalesce(existing.settled_amount_base, 0)::numeric(20,6) as settled_amount_base,
                existing.last_settled_at
              from {BridgeLinesTableName} bridge
              left join bills b
                on b.company_id = bridge.company_id
               and b.id = bridge.bill_id
              left join lateral (
                select oi.id
                from ap_open_items oi
                where oi.company_id = bridge.company_id
                  and oi.source_type = 'bill'
                  and oi.source_id = bridge.bill_id
                order by oi.created_at desc, oi.id
                limit 1
              ) oi on true
              left join journal_entries je
                on je.company_id = bridge.company_id
               and je.id = bridge.journal_entry_id
              left join {SettlementLinesTableName} existing
                on existing.company_id = bridge.company_id
               and existing.bridge_line_id = bridge.id
              where bridge.company_id = @company_id
                and bridge.receipt_id = @receipt_id
            ),
            classified as (
              select
                *,
                greatest(round(bridge_amount_base - settled_amount_base, 6), 0)::numeric(20,6) as remaining_amount_base,
                case
                  when bridge_status <> 'posted' then '{ReceiptGrIrApSettlementStatusPolicy.BlockedGrIrNotPosted}'
                  when journal_entry_id is null or journal_status <> 'posted' then '{ReceiptGrIrApSettlementStatusPolicy.BlockedJournalNotPosted}'
                  when bill_status <> 'posted' then '{ReceiptGrIrApSettlementStatusPolicy.BlockedBillNotPosted}'
                  when ap_open_item_id is null then '{ReceiptGrIrApSettlementStatusPolicy.BlockedMissingApOpenItem}'
                  when round(settled_amount_base, 6) > round(bridge_amount_base, 6) then '{ReceiptGrIrApSettlementStatusPolicy.BlockedAmountExceeded}'
                  when round(settled_amount_base, 6) >= round(bridge_amount_base, 6) then '{ReceiptGrIrApSettlementStatusPolicy.Settled}'
                  when round(settled_amount_base, 6) > 0 then '{ReceiptGrIrApSettlementStatusPolicy.PartiallySettled}'
                  else '{ReceiptGrIrApSettlementStatusPolicy.EligibleNotSettled}'
                end as settlement_status,
                case
                  when bridge_status <> 'posted' then 'grir_bridge_not_posted'
                  when journal_entry_id is null or journal_status <> 'posted' then 'journal_not_posted'
                  when bill_status <> 'posted' then 'bill_not_posted'
                  when ap_open_item_id is null then 'missing_ap_open_item'
                  when round(settled_amount_base, 6) > round(bridge_amount_base, 6) then 'settled_amount_exceeds_bridge'
                  else null
                end as blocked_reason_code
              from bridge_truth
            )
            insert into {SettlementLinesTableName} (
              id,
              company_id,
              receipt_id,
              receipt_line_number,
              bridge_line_id,
              journal_entry_id,
              journal_entry_display_number,
              bill_id,
              bill_line_number,
              ap_open_item_id,
              item_id,
              warehouse_id,
              uom_code,
              settlement_quantity,
              settlement_amount_base,
              settled_quantity,
              settled_amount_base,
              remaining_amount_base,
              settlement_status,
              blocked_reason_code,
              refreshed_by_user_id,
              refreshed_at,
              last_settled_at
            )
            select
              gen_random_uuid(),
              company_id,
              receipt_id,
              receipt_line_number,
              bridge_line_id,
              journal_entry_id,
              journal_entry_display_number,
              bill_id,
              bill_line_number,
              ap_open_item_id,
              item_id,
              warehouse_id,
              uom_code,
              bridge_quantity,
              bridge_amount_base,
              settled_quantity,
              settled_amount_base,
              remaining_amount_base,
              settlement_status,
              blocked_reason_code,
              @user_id,
              now(),
              last_settled_at
            from classified
            on conflict (company_id, bridge_line_id)
            do update set
              journal_entry_id = excluded.journal_entry_id,
              journal_entry_display_number = excluded.journal_entry_display_number,
              bill_id = excluded.bill_id,
              bill_line_number = excluded.bill_line_number,
              ap_open_item_id = excluded.ap_open_item_id,
              item_id = excluded.item_id,
              warehouse_id = excluded.warehouse_id,
              uom_code = excluded.uom_code,
              settlement_quantity = excluded.settlement_quantity,
              settlement_amount_base = excluded.settlement_amount_base,
              settled_quantity = excluded.settled_quantity,
              settled_amount_base = excluded.settled_amount_base,
              remaining_amount_base = excluded.remaining_amount_base,
              settlement_status = excluded.settlement_status,
              blocked_reason_code = excluded.blocked_reason_code,
              refreshed_by_user_id = excluded.refreshed_by_user_id,
              refreshed_at = excluded.refreshed_at,
              last_settled_at = excluded.last_settled_at;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ReceiptGrIrApSettlementExecutionResult?> TryLoadSettlementExecutionResultAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select
              batch.id,
              batch.requested_amount_base,
              batch.settled_quantity,
              batch.settled_amount_base,
              batch.line_count
            from {SettlementBatchesTableName} batch
            where batch.company_id = @company_id
              and batch.receipt_id = @receipt_id
              and batch.idempotency_key = @idempotency_key
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var batchId = reader.GetGuid(reader.GetOrdinal("id"));
        var requestedAmountBase = reader.GetFieldValue<decimal>(reader.GetOrdinal("requested_amount_base"));
        var settledQuantity = reader.GetFieldValue<decimal>(reader.GetOrdinal("settled_quantity"));
        var settledAmountBase = reader.GetFieldValue<decimal>(reader.GetOrdinal("settled_amount_base"));
        var lineCount = reader.GetInt32(reader.GetOrdinal("line_count"));
        await reader.DisposeAsync();

        var summary = await LoadReceiptSettlementSummaryAsync(scope, companyId, receiptDocumentId, cancellationToken)
            ?? BuildEmptyReceiptSummary(receiptDocumentId);

        return new ReceiptGrIrApSettlementExecutionResult(
            receiptDocumentId,
            batchId,
            idempotencyKey,
            Round6(requestedAmountBase),
            Round6(settledQuantity),
            Round6(settledAmountBase),
            lineCount,
            summary);
    }

    private static async Task<IReadOnlyList<ExecutableSettlementLine>> LoadExecutableSettlementLinesAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select
              line.id,
              line.bridge_line_id,
              line.bill_id,
              line.ap_open_item_id,
              line.settlement_quantity,
              line.settlement_amount_base,
              line.settled_quantity,
              line.settled_amount_base,
              line.remaining_amount_base
            from {SettlementLinesTableName} line
            where line.company_id = @company_id
              and line.receipt_id = @receipt_id
              and line.settlement_status in (
                '{ReceiptGrIrApSettlementStatusPolicy.EligibleNotSettled}',
                '{ReceiptGrIrApSettlementStatusPolicy.PartiallySettled}'
              )
              and round(line.remaining_amount_base, 6) > 0
            order by line.receipt_line_number, line.bill_line_number, line.id
            for update;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);

        var lines = new List<ExecutableSettlementLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new ExecutableSettlementLine(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("bridge_line_id")),
                reader.GetGuid(reader.GetOrdinal("bill_id")),
                reader.IsDBNull(reader.GetOrdinal("ap_open_item_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("ap_open_item_id")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settlement_quantity"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settlement_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settled_quantity"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settled_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("remaining_amount_base")))));
        }

        return lines;
    }

    private static IReadOnlyList<SettlementAllocation> BuildSettlementAllocations(
        IReadOnlyList<ExecutableSettlementLine> executableLines,
        decimal requestedAmountBase)
    {
        var remainingRequest = Round6(requestedAmountBase);
        var allocations = new List<SettlementAllocation>();

        foreach (var line in executableLines)
        {
            if (remainingRequest <= 0)
            {
                break;
            }

            var amountBase = remainingRequest >= line.RemainingAmountBase
                ? line.RemainingAmountBase
                : remainingRequest;
            amountBase = Round6(amountBase);
            var remainingQuantity = Round6(line.SettlementQuantity - line.SettledQuantity);
            var quantity = amountBase >= line.RemainingAmountBase
                ? remainingQuantity
                : Round6(line.SettlementQuantity * amountBase / line.SettlementAmountBase);
            if (quantity > remainingQuantity)
            {
                quantity = remainingQuantity;
            }

            if (amountBase <= 0 || quantity <= 0)
            {
                throw new InvalidOperationException("GR/IR settlement allocation produced a non-positive slice. Refresh settlement control truth and retry.");
            }

            allocations.Add(new SettlementAllocation(
                line.SettlementLineId,
                line.BridgeLineId,
                line.BillId,
                line.ApOpenItemId,
                Round6(quantity),
                amountBase));
            remainingRequest = Round6(remainingRequest - amountBase);
        }

        if (remainingRequest > 0)
        {
            throw new InvalidOperationException("GR/IR settlement allocation could not consume the requested amount.");
        }

        return allocations;
    }

    private static async Task CreateSettlementBatchAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        Guid batchId,
        string idempotencyKey,
        decimal requestedAmountBase,
        IReadOnlyList<SettlementAllocation> allocations,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            insert into {SettlementBatchesTableName} (
              id,
              company_id,
              receipt_id,
              idempotency_key,
              status,
              requested_amount_base,
              settled_quantity,
              settled_amount_base,
              line_count,
              created_by_user_id
            )
            values (
              @batch_id,
              @company_id,
              @receipt_id,
              @idempotency_key,
              'posted',
              @requested_amount_base,
              @settled_quantity,
              @settled_amount_base,
              @line_count,
              @created_by_user_id
            );
            """);
        command.Parameters.AddWithValue("batch_id", batchId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("requested_amount_base", requestedAmountBase);
        command.Parameters.AddWithValue("settled_quantity", Round6(allocations.Sum(static allocation => allocation.SettledQuantity)));
        command.Parameters.AddWithValue("settled_amount_base", Round6(allocations.Sum(static allocation => allocation.SettledAmountBase)));
        command.Parameters.AddWithValue("line_count", allocations.Count);
        command.Parameters.AddWithValue("created_by_user_id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSettlementBatchLinesAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid batchId,
        IReadOnlyList<SettlementAllocation> allocations,
        CancellationToken cancellationToken)
    {
        foreach (var allocation in allocations)
        {
            await using var command = scope.CreateCommand(
                $"""
                insert into {SettlementBatchLinesTableName} (
                  company_id,
                  settlement_batch_id,
                  settlement_line_id,
                  bridge_line_id,
                  bill_id,
                  ap_open_item_id,
                  settled_quantity,
                  settled_amount_base
                )
                values (
                  @company_id,
                  @settlement_batch_id,
                  @settlement_line_id,
                  @bridge_line_id,
                  @bill_id,
                  @ap_open_item_id,
                  @settled_quantity,
                  @settled_amount_base
                );
                """);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("settlement_batch_id", batchId);
            command.Parameters.AddWithValue("settlement_line_id", allocation.SettlementLineId);
            command.Parameters.AddWithValue("bridge_line_id", allocation.BridgeLineId);
            command.Parameters.AddWithValue("bill_id", allocation.BillId);
            command.Parameters.AddWithValue(
                "ap_open_item_id",
                allocation.ApOpenItemId.HasValue ? allocation.ApOpenItemId.Value : DBNull.Value);
            command.Parameters.AddWithValue("settled_quantity", allocation.SettledQuantity);
            command.Parameters.AddWithValue("settled_amount_base", allocation.SettledAmountBase);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ApplySettlementAllocationsAsync(
        PostgresCommandScope scope,
        Guid companyId,
        IReadOnlyList<SettlementAllocation> allocations,
        CancellationToken cancellationToken)
    {
        foreach (var allocation in allocations)
        {
            await using var command = scope.CreateCommand(
                $"""
                update {SettlementLinesTableName}
                set settled_quantity = round(settled_quantity + @settled_quantity, 6),
                    settled_amount_base = round(settled_amount_base + @settled_amount_base, 6),
                    remaining_amount_base = greatest(round(remaining_amount_base - @settled_amount_base, 6), 0),
                    last_settled_at = now()
                where company_id = @company_id
                  and id = @settlement_line_id
                  and settlement_status in (
                    '{ReceiptGrIrApSettlementStatusPolicy.EligibleNotSettled}',
                    '{ReceiptGrIrApSettlementStatusPolicy.PartiallySettled}'
                  )
                  and round(remaining_amount_base, 6) >= @settled_amount_base;
                """);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("settlement_line_id", allocation.SettlementLineId);
            command.Parameters.AddWithValue("settled_quantity", allocation.SettledQuantity);
            command.Parameters.AddWithValue("settled_amount_base", allocation.SettledAmountBase);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("GR/IR settlement slice was already consumed or is no longer eligible. Refresh settlement control truth and retry.");
            }
        }
    }

    private static async Task<ReceiptGrIrApOpenItemClearingResult?> TryLoadExistingOpenItemClearingResultAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select id
            from settlement_applications
            where company_id = @company_id
              and source_type = 'receipt_grir_ap_settlement'
              and source_id = @settlement_batch_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);

        var existing = await command.ExecuteScalarAsync(cancellationToken);
        if (existing is null || existing == DBNull.Value)
        {
            return null;
        }

        await RefreshReceiptSettlementOpenItemClearingStatusesAsync(scope, companyId, receiptDocumentId, cancellationToken);
        return await LoadOpenItemClearingResultAsync(
            scope,
            companyId,
            receiptDocumentId,
            settlementBatchId,
            cancellationToken);
    }

    private static async Task<OpenItemClearingBatchHeader?> LoadOpenItemClearingBatchHeaderAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select
              status,
              journal_status,
              open_item_clearing_status
            from {SettlementBatchesTableName}
            where company_id = @company_id
              and receipt_id = @receipt_id
              and id = @settlement_batch_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OpenItemClearingBatchHeader(
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetString(reader.GetOrdinal("journal_status")),
                reader.GetString(reader.GetOrdinal("open_item_clearing_status")))
            : null;
    }

    private static async Task<IReadOnlyList<OpenItemClearingAllocation>> LoadOpenItemClearingAllocationsAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select
              batch_line.ap_open_item_id,
              oi.vendor_id,
              oi.document_currency_code,
              oi.base_currency_code,
              oi.open_amount_tx,
              oi.open_amount_base,
              oi.status,
              sum(batch_line.settled_amount_base)::numeric(20,6) as amount_base
            from {SettlementBatchLinesTableName} batch_line
            join ap_open_items oi
              on oi.company_id = batch_line.company_id
             and oi.id = batch_line.ap_open_item_id
            where batch_line.company_id = @company_id
              and batch_line.settlement_batch_id = @settlement_batch_id
            group by
              batch_line.ap_open_item_id,
              oi.vendor_id,
              oi.document_currency_code,
              oi.base_currency_code,
              oi.open_amount_tx,
              oi.open_amount_base,
              oi.status
            order by batch_line.ap_open_item_id;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);

        var allocations = new List<OpenItemClearingAllocation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            allocations.Add(new OpenItemClearingAllocation(
                reader.GetGuid(reader.GetOrdinal("ap_open_item_id")),
                reader.GetGuid(reader.GetOrdinal("vendor_id")),
                reader.GetString(reader.GetOrdinal("document_currency_code")),
                reader.GetString(reader.GetOrdinal("base_currency_code")),
                Round6(reader.GetDecimal(reader.GetOrdinal("open_amount_tx"))),
                Round6(reader.GetDecimal(reader.GetOrdinal("open_amount_base"))),
                reader.GetString(reader.GetOrdinal("status")),
                Round6(reader.GetDecimal(reader.GetOrdinal("amount_base")))));
        }

        return allocations;
    }

    private static async Task ApplyOpenItemClearingAllocationAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid userId,
        Guid settlementBatchId,
        OpenItemClearingAllocation allocation,
        CancellationToken cancellationToken)
    {
        if (allocation.Status is not ("open" or "partially_applied"))
        {
            throw new InvalidOperationException("AP open item is not open for GR/IR settlement clearing.");
        }

        if (!string.Equals(allocation.DocumentCurrencyCode, allocation.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GR/IR AP open item clearing does not yet support foreign-currency open items.");
        }

        if (allocation.AmountBase > allocation.OpenAmountBase || allocation.AmountBase > allocation.OpenAmountTx)
        {
            throw new InvalidOperationException("GR/IR AP open item clearing amount exceeds the remaining AP open item balance.");
        }

        var nextOpenAmountTx = Round6(allocation.OpenAmountTx - allocation.AmountBase);
        var nextOpenAmountBase = Round6(allocation.OpenAmountBase - allocation.AmountBase);
        var nextStatus = nextOpenAmountTx == 0m && nextOpenAmountBase == 0m
            ? "closed"
            : "partially_applied";

        await using (var insertCommand = scope.CreateCommand(
                         """
                         insert into settlement_applications (
                           id,
                           company_id,
                           application_type,
                           source_type,
                           source_id,
                           target_open_item_type,
                           target_open_item_id,
                           applied_amount_tx,
                           applied_amount_base,
                           settlement_fx_rate,
                           realized_fx_amount,
                           created_at,
                           created_by_user_id
                         )
                         values (
                           gen_random_uuid(),
                           @company_id,
                           'receipt_grir_ap_settlement',
                           'receipt_grir_ap_settlement',
                           @settlement_batch_id,
                           'ap_open_item',
                           @ap_open_item_id,
                           @applied_amount_tx,
                           @applied_amount_base,
                           null,
                           null,
                           now(),
                           @created_by_user_id
                         );
                         """))
        {
            insertCommand.Parameters.AddWithValue("company_id", companyId);
            insertCommand.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);
            insertCommand.Parameters.AddWithValue("ap_open_item_id", allocation.ApOpenItemId);
            insertCommand.Parameters.AddWithValue("applied_amount_tx", allocation.AmountBase);
            insertCommand.Parameters.AddWithValue("applied_amount_base", allocation.AmountBase);
            insertCommand.Parameters.AddWithValue("created_by_user_id", userId);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var updateCommand = scope.CreateCommand(
            """
            update ap_open_items
            set open_amount_tx = @open_amount_tx,
                open_amount_base = @open_amount_base,
                status = @status,
                updated_at = now()
            where company_id = @company_id
              and id = @ap_open_item_id
              and open_amount_tx >= @applied_amount_tx
              and open_amount_base >= @applied_amount_base
              and status in ('open', 'partially_applied');
            """);
        updateCommand.Parameters.AddWithValue("open_amount_tx", nextOpenAmountTx);
        updateCommand.Parameters.AddWithValue("open_amount_base", nextOpenAmountBase);
        updateCommand.Parameters.AddWithValue("status", nextStatus);
        updateCommand.Parameters.AddWithValue("company_id", companyId);
        updateCommand.Parameters.AddWithValue("ap_open_item_id", allocation.ApOpenItemId);
        updateCommand.Parameters.AddWithValue("applied_amount_tx", allocation.AmountBase);
        updateCommand.Parameters.AddWithValue("applied_amount_base", allocation.AmountBase);
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("AP open item balance changed before GR/IR settlement clearing could be recorded.");
        }
    }

    private static async Task MarkOpenItemClearingBatchClearedAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid userId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            update {SettlementBatchesTableName}
            set open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.Cleared}',
                open_item_clearing_blocked_reason_code = null,
                open_item_cleared_by_user_id = coalesce(open_item_cleared_by_user_id, @cleared_by_user_id),
                open_item_cleared_at = coalesce(open_item_cleared_at, now()),
                open_item_clearing_refreshed_at = now()
            where company_id = @company_id
              and id = @settlement_batch_id
              and open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.NotCleared}';
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);
        command.Parameters.AddWithValue("cleared_by_user_id", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("GR/IR settlement batch AP open-item clearing state changed before clearing could complete.");
        }
    }

    private static async Task<ReceiptGrIrApOpenItemClearingResult> LoadOpenItemClearingResultAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              batch.open_item_clearing_status,
              count(sa.id)::int as application_count,
              coalesce(sum(sa.applied_amount_tx), 0)::numeric(20,6) as cleared_amount_tx,
              coalesce(sum(sa.applied_amount_base), 0)::numeric(20,6) as cleared_amount_base
            from receipt_grir_ap_settlement_batches batch
            left join settlement_applications sa
              on sa.company_id = batch.company_id
             and sa.source_type = 'receipt_grir_ap_settlement'
             and sa.source_id = batch.id
            where batch.company_id = @company_id
              and batch.receipt_id = @receipt_id
              and batch.id = @settlement_batch_id
            group by batch.open_item_clearing_status;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("GR/IR settlement batch was not found for AP open-item clearing result.");
        }

        var clearingStatus = reader.GetString(reader.GetOrdinal("open_item_clearing_status"));
        var applicationCount = reader.GetInt32(reader.GetOrdinal("application_count"));
        var clearedAmountTx = Round6(reader.GetDecimal(reader.GetOrdinal("cleared_amount_tx")));
        var clearedAmountBase = Round6(reader.GetDecimal(reader.GetOrdinal("cleared_amount_base")));
        await reader.DisposeAsync();

        var summary = await LoadReceiptSettlementSummaryAsync(scope, companyId, receiptDocumentId, cancellationToken)
            ?? BuildEmptyReceiptSummary(receiptDocumentId);

        return new ReceiptGrIrApOpenItemClearingResult(
            receiptDocumentId,
            settlementBatchId,
            clearingStatus,
            applicationCount,
            clearedAmountTx,
            clearedAmountBase,
            summary);
    }

    private static async Task<ReceiptGrIrApOpenItemClearingReversalResult?> TryLoadExistingOpenItemClearingReversalResultAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select open_item_clearing_status
            from {SettlementBatchesTableName}
            where company_id = @company_id
              and receipt_id = @receipt_id
              and id = @settlement_batch_id
              and open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.Reversed}'
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);

        var existing = await command.ExecuteScalarAsync(cancellationToken);
        return existing is null || existing == DBNull.Value
            ? null
            : await LoadOpenItemClearingReversalResultAsync(
                scope,
                companyId,
                receiptDocumentId,
                settlementBatchId,
                cancellationToken);
    }

    private static async Task<IReadOnlyList<OpenItemClearingApplication>> LoadOpenItemClearingApplicationsAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              id,
              target_open_item_id,
              applied_amount_tx,
              applied_amount_base
            from settlement_applications
            where company_id = @company_id
              and source_type = 'receipt_grir_ap_settlement'
              and source_id = @settlement_batch_id
              and target_open_item_type = 'ap_open_item'
            order by created_at desc, id desc;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);

        var applications = new List<OpenItemClearingApplication>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applications.Add(new OpenItemClearingApplication(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("target_open_item_id")),
                Round6(reader.GetDecimal(reader.GetOrdinal("applied_amount_tx"))),
                Round6(reader.GetDecimal(reader.GetOrdinal("applied_amount_base")))));
        }

        return applications;
    }

    private static async Task ReverseOpenItemClearingApplicationAsync(
        PostgresCommandScope scope,
        Guid companyId,
        OpenItemClearingApplication application,
        CancellationToken cancellationToken)
    {
        await using var updateCommand = scope.CreateCommand(
            """
            update ap_open_items
            set open_amount_tx = least(original_amount_tx, open_amount_tx + @applied_amount_tx),
                open_amount_base = least(original_amount_base, open_amount_base + @applied_amount_base),
                status = case
                    when least(original_amount_tx, open_amount_tx + @applied_amount_tx) <= 0 then 'closed'
                    when least(original_amount_tx, open_amount_tx + @applied_amount_tx) >= original_amount_tx then 'open'
                    else 'partially_applied'
                end,
                updated_at = now()
            where company_id = @company_id
              and id = @ap_open_item_id
              and status <> 'voided';
            """);
        updateCommand.Parameters.AddWithValue("company_id", companyId);
        updateCommand.Parameters.AddWithValue("ap_open_item_id", application.ApOpenItemId);
        updateCommand.Parameters.AddWithValue("applied_amount_tx", application.AppliedAmountTx);
        updateCommand.Parameters.AddWithValue("applied_amount_base", application.AppliedAmountBase);
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("AP open item could not be restored for GR/IR settlement clearing reversal.");
        }
    }

    private static async Task DeleteOpenItemClearingApplicationsAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            delete from settlement_applications
            where company_id = @company_id
              and source_type = 'receipt_grir_ap_settlement'
              and source_id = @settlement_batch_id;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkOpenItemClearingBatchReversedAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid userId,
        Guid settlementBatchId,
        int applicationCount,
        decimal restoredAmountTx,
        decimal restoredAmountBase,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            update {SettlementBatchesTableName}
            set open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.Reversed}',
                open_item_clearing_blocked_reason_code = null,
                open_item_reversed_by_user_id = @reversed_by_user_id,
                open_item_reversed_at = now(),
                open_item_reversed_application_count = @application_count,
                open_item_reversed_amount_tx = @restored_amount_tx,
                open_item_reversed_amount_base = @restored_amount_base,
                open_item_clearing_refreshed_at = now()
            where company_id = @company_id
              and id = @settlement_batch_id
              and open_item_clearing_status in (
                '{ReceiptGrIrApOpenItemClearingStatusPolicy.ClearingStale}',
                '{ReceiptGrIrApOpenItemClearingStatusPolicy.ClearingInconsistent}'
              );
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);
        command.Parameters.AddWithValue("reversed_by_user_id", userId);
        command.Parameters.AddWithValue("application_count", applicationCount);
        command.Parameters.AddWithValue("restored_amount_tx", restoredAmountTx);
        command.Parameters.AddWithValue("restored_amount_base", restoredAmountBase);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("GR/IR settlement batch AP open-item clearing state changed before reversal could complete.");
        }
    }

    private static async Task<ReceiptGrIrApOpenItemClearingReversalResult> LoadOpenItemClearingReversalResultAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select
              open_item_clearing_status,
              coalesce(open_item_reversed_application_count, 0)::int as reversed_application_count,
              coalesce(open_item_reversed_amount_tx, 0)::numeric(20,6) as restored_amount_tx,
              coalesce(open_item_reversed_amount_base, 0)::numeric(20,6) as restored_amount_base
            from {SettlementBatchesTableName}
            where company_id = @company_id
              and receipt_id = @receipt_id
              and id = @settlement_batch_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("GR/IR settlement batch was not found for AP open-item clearing reversal result.");
        }

        var clearingStatus = reader.GetString(reader.GetOrdinal("open_item_clearing_status"));
        var applicationCount = reader.GetInt32(reader.GetOrdinal("reversed_application_count"));
        var restoredAmountTx = Round6(reader.GetDecimal(reader.GetOrdinal("restored_amount_tx")));
        var restoredAmountBase = Round6(reader.GetDecimal(reader.GetOrdinal("restored_amount_base")));
        await reader.DisposeAsync();

        var summary = await LoadReceiptSettlementSummaryAsync(scope, companyId, receiptDocumentId, cancellationToken)
            ?? BuildEmptyReceiptSummary(receiptDocumentId);

        return new ReceiptGrIrApOpenItemClearingReversalResult(
            receiptDocumentId,
            settlementBatchId,
            clearingStatus,
            applicationCount,
            restoredAmountTx,
            restoredAmountBase,
            summary);
    }

    private static async Task<ReceiptGrIrApSettlementSummary?> LoadReceiptSettlementSummaryAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        var summaries = await LoadReceiptSettlementSummariesAsync(
            scope,
            companyId,
            [receiptDocumentId],
            cancellationToken);

        return summaries.TryGetValue(receiptDocumentId, out var summary) ? summary : null;
    }

    private static async Task<IReadOnlyDictionary<Guid, ReceiptGrIrApSettlementSummary>> LoadReceiptSettlementSummariesAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid[] receiptDocumentIds,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            BuildSummarySql("receipt_id", "receipt_document_ids"));
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("receipt_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = receiptDocumentIds
        });

        var summaries = new Dictionary<Guid, ReceiptGrIrApSettlementSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var receiptDocumentId = reader.GetGuid(reader.GetOrdinal("document_id"));
            var settlementLineCount = reader.GetInt32(reader.GetOrdinal("settlement_line_count"));
            var eligibleLineCount = reader.GetInt32(reader.GetOrdinal("eligible_line_count"));
            var blockedLineCount = reader.GetInt32(reader.GetOrdinal("blocked_line_count"));
            var partiallySettledLineCount = reader.GetInt32(reader.GetOrdinal("partially_settled_line_count"));
            var settledLineCount = reader.GetInt32(reader.GetOrdinal("settled_line_count"));
            var settlementBatchCount = reader.GetInt32(reader.GetOrdinal("settlement_batch_count"));
            var journalNotPostedBatchCount = reader.GetInt32(reader.GetOrdinal("journal_not_posted_batch_count"));
            var journalPostedBatchCount = reader.GetInt32(reader.GetOrdinal("journal_posted_batch_count"));
            var journalStaleBatchCount = reader.GetInt32(reader.GetOrdinal("journal_stale_batch_count"));
            var journalInconsistentBatchCount = reader.GetInt32(reader.GetOrdinal("journal_inconsistent_batch_count"));
            var openItemNotClearedBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_not_cleared_batch_count"));
            var openItemClearedBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_cleared_batch_count"));
            var openItemReversedBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_reversed_batch_count"));
            var openItemBlockedBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_blocked_batch_count"));
            var openItemStaleBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_stale_batch_count"));
            var openItemInconsistentBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_inconsistent_batch_count"));
            var purchaseVarianceLineCount = reader.GetInt32(reader.GetOrdinal("purchase_variance_line_count"));
            var purchaseVarianceCandidateLineCount = reader.GetInt32(reader.GetOrdinal("purchase_variance_candidate_line_count"));
            var purchaseVarianceNoVarianceLineCount = reader.GetInt32(reader.GetOrdinal("purchase_variance_no_variance_line_count"));
            var purchaseVarianceBlockedLineCount = reader.GetInt32(reader.GetOrdinal("purchase_variance_blocked_line_count"));
            summaries[receiptDocumentId] = new ReceiptGrIrApSettlementSummary(
                receiptDocumentId,
                ReceiptGrIrApSettlementStatusPolicy.ResolveSummaryStatus(
                    settlementLineCount,
                    eligibleLineCount,
                    blockedLineCount,
                    partiallySettledLineCount,
                    settledLineCount),
                settlementLineCount,
                eligibleLineCount,
                blockedLineCount,
                reader.GetInt32(reader.GetOrdinal("blocked_grir_not_posted_line_count")),
                reader.GetInt32(reader.GetOrdinal("blocked_bill_not_posted_line_count")),
                reader.GetInt32(reader.GetOrdinal("blocked_missing_ap_open_item_line_count")),
                reader.GetInt32(reader.GetOrdinal("blocked_journal_not_posted_line_count")),
                reader.GetInt32(reader.GetOrdinal("blocked_amount_exceeded_line_count")),
                partiallySettledLineCount,
                settledLineCount,
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settlement_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("eligible_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settled_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("remaining_amount_base"))),
                settlementBatchCount,
                journalNotPostedBatchCount,
                journalPostedBatchCount,
                journalStaleBatchCount,
                journalInconsistentBatchCount,
                ReceiptGrIrApSettlementJournalStatusPolicy.ResolveSummaryStatus(
                    settlementBatchCount,
                    journalNotPostedBatchCount,
                    journalPostedBatchCount,
                    journalStaleBatchCount,
                    journalInconsistentBatchCount),
                reader.IsDBNull(reader.GetOrdinal("last_journal_refreshed_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_journal_refreshed_at")),
                openItemNotClearedBatchCount,
                openItemClearedBatchCount,
                openItemReversedBatchCount,
                openItemBlockedBatchCount,
                openItemStaleBatchCount,
                openItemInconsistentBatchCount,
                ReceiptGrIrApOpenItemClearingStatusPolicy.ResolveSummaryStatus(
                    settlementBatchCount,
                    openItemNotClearedBatchCount,
                    openItemClearedBatchCount,
                    openItemReversedBatchCount,
                    openItemBlockedBatchCount,
                    openItemStaleBatchCount,
                    openItemInconsistentBatchCount),
                reader.IsDBNull(reader.GetOrdinal("last_open_item_cleared_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_open_item_cleared_at")),
                reader.IsDBNull(reader.GetOrdinal("last_open_item_reversed_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_open_item_reversed_at")),
                purchaseVarianceLineCount,
                purchaseVarianceCandidateLineCount,
                purchaseVarianceNoVarianceLineCount,
                purchaseVarianceBlockedLineCount,
                ReceiptGrIrApPurchaseVarianceStatusPolicy.ResolveSummaryStatus(
                    purchaseVarianceLineCount,
                    purchaseVarianceCandidateLineCount,
                    purchaseVarianceNoVarianceLineCount,
                    purchaseVarianceBlockedLineCount),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("purchase_variance_amount_base"))),
                reader.IsDBNull(reader.GetOrdinal("last_purchase_variance_refreshed_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_purchase_variance_refreshed_at")),
                reader.IsDBNull(reader.GetOrdinal("last_refreshed_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_refreshed_at")),
                reader.IsDBNull(reader.GetOrdinal("last_settled_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_settled_at")));
        }

        foreach (var receiptDocumentId in receiptDocumentIds)
        {
            summaries.TryAdd(receiptDocumentId, BuildEmptyReceiptSummary(receiptDocumentId));
        }

        return summaries;
    }

    private static async Task<IReadOnlyList<ReceiptGrIrApSettlementBatchSummary>> LoadReceiptSettlementBatchSummariesAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select
              batch.receipt_id,
              batch.id as settlement_batch_id,
              batch.status,
              batch.requested_amount_base,
              batch.settled_quantity,
              batch.settled_amount_base,
              batch.line_count,
              batch.journal_status,
              batch.journal_entry_id,
              batch.journal_entry_display_number,
              batch.journal_posted_at,
              batch.journal_blocked_reason_code,
              batch.open_item_clearing_status,
              batch.open_item_clearing_blocked_reason_code,
              batch.open_item_cleared_at,
              batch.open_item_reversed_at,
              batch.open_item_reversed_application_count,
              batch.open_item_reversed_amount_tx,
              batch.open_item_reversed_amount_base,
              batch.created_at
            from {SettlementBatchesTableName} batch
            where batch.company_id = @company_id
              and batch.receipt_id = @receipt_id
            order by batch.created_at desc, batch.id;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);

        var summaries = new List<ReceiptGrIrApSettlementBatchSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new ReceiptGrIrApSettlementBatchSummary(
                reader.GetGuid(reader.GetOrdinal("receipt_id")),
                reader.GetGuid(reader.GetOrdinal("settlement_batch_id")),
                reader.GetString(reader.GetOrdinal("status")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("requested_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settled_quantity"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settled_amount_base"))),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetString(reader.GetOrdinal("journal_status")),
                reader.IsDBNull(reader.GetOrdinal("journal_entry_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("journal_entry_id")),
                reader.IsDBNull(reader.GetOrdinal("journal_entry_display_number"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("journal_entry_display_number")),
                reader.IsDBNull(reader.GetOrdinal("journal_posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("journal_posted_at")),
                reader.IsDBNull(reader.GetOrdinal("journal_blocked_reason_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("journal_blocked_reason_code")),
                reader.GetString(reader.GetOrdinal("open_item_clearing_status")),
                reader.IsDBNull(reader.GetOrdinal("open_item_clearing_blocked_reason_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("open_item_clearing_blocked_reason_code")),
                reader.IsDBNull(reader.GetOrdinal("open_item_cleared_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("open_item_cleared_at")),
                reader.IsDBNull(reader.GetOrdinal("open_item_reversed_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("open_item_reversed_at")),
                reader.GetInt32(reader.GetOrdinal("open_item_reversed_application_count")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("open_item_reversed_amount_tx"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("open_item_reversed_amount_base"))),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at"))));
        }

        return summaries;
    }

    private static async Task<IReadOnlyList<ReceiptGrIrApPurchaseVarianceLineSummary>> LoadReceiptPurchaseVarianceLineSummariesAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select
              variance.receipt_id,
              variance.receipt_line_number,
              variance.settlement_batch_id,
              variance.settlement_batch_line_id,
              variance.bill_id,
              variance.bill_line_number,
              variance.item_id,
              variance.warehouse_id,
              variance.uom_code,
              variance.settled_quantity,
              variance.grir_amount_base,
              variance.bill_amount_base,
              variance.variance_amount_base,
              variance.variance_status,
              variance.blocked_reason_code,
              variance.refreshed_at
            from {PurchaseVarianceLinesTableName} variance
            where variance.company_id = @company_id
              and variance.receipt_id = @receipt_id
            order by variance.receipt_line_number, variance.bill_line_number, variance.settlement_batch_id;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);

        var summaries = new List<ReceiptGrIrApPurchaseVarianceLineSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new ReceiptGrIrApPurchaseVarianceLineSummary(
                reader.GetGuid(reader.GetOrdinal("receipt_id")),
                reader.GetInt32(reader.GetOrdinal("receipt_line_number")),
                reader.GetGuid(reader.GetOrdinal("settlement_batch_id")),
                reader.GetGuid(reader.GetOrdinal("settlement_batch_line_id")),
                reader.GetGuid(reader.GetOrdinal("bill_id")),
                reader.GetInt32(reader.GetOrdinal("bill_line_number")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settled_quantity"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("grir_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("bill_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("variance_amount_base"))),
                reader.GetString(reader.GetOrdinal("variance_status")),
                reader.IsDBNull(reader.GetOrdinal("blocked_reason_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("blocked_reason_code")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("refreshed_at"))));
        }

        return summaries;
    }

    private static async Task<BillGrIrApSettlementSummary?> LoadBillSettlementSummaryAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            BuildSummarySql("bill_id", "bill_document_ids"));
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("bill_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = [billDocumentId]
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var settlementLineCount = reader.GetInt32(reader.GetOrdinal("settlement_line_count"));
        var eligibleLineCount = reader.GetInt32(reader.GetOrdinal("eligible_line_count"));
        var blockedLineCount = reader.GetInt32(reader.GetOrdinal("blocked_line_count"));
        var partiallySettledLineCount = reader.GetInt32(reader.GetOrdinal("partially_settled_line_count"));
        var settledLineCount = reader.GetInt32(reader.GetOrdinal("settled_line_count"));
        var settlementBatchCount = reader.GetInt32(reader.GetOrdinal("settlement_batch_count"));
        var journalNotPostedBatchCount = reader.GetInt32(reader.GetOrdinal("journal_not_posted_batch_count"));
        var journalPostedBatchCount = reader.GetInt32(reader.GetOrdinal("journal_posted_batch_count"));
        var journalStaleBatchCount = reader.GetInt32(reader.GetOrdinal("journal_stale_batch_count"));
        var journalInconsistentBatchCount = reader.GetInt32(reader.GetOrdinal("journal_inconsistent_batch_count"));
        var openItemNotClearedBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_not_cleared_batch_count"));
        var openItemClearedBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_cleared_batch_count"));
        var openItemReversedBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_reversed_batch_count"));
        var openItemBlockedBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_blocked_batch_count"));
        var openItemStaleBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_stale_batch_count"));
        var openItemInconsistentBatchCount = reader.GetInt32(reader.GetOrdinal("open_item_inconsistent_batch_count"));
        var purchaseVarianceLineCount = reader.GetInt32(reader.GetOrdinal("purchase_variance_line_count"));
        var purchaseVarianceCandidateLineCount = reader.GetInt32(reader.GetOrdinal("purchase_variance_candidate_line_count"));
        var purchaseVarianceNoVarianceLineCount = reader.GetInt32(reader.GetOrdinal("purchase_variance_no_variance_line_count"));
        var purchaseVarianceBlockedLineCount = reader.GetInt32(reader.GetOrdinal("purchase_variance_blocked_line_count"));

        return new BillGrIrApSettlementSummary(
            billDocumentId,
            ReceiptGrIrApSettlementStatusPolicy.ResolveSummaryStatus(
                settlementLineCount,
                eligibleLineCount,
                blockedLineCount,
                partiallySettledLineCount,
                settledLineCount),
            settlementLineCount,
            eligibleLineCount,
            blockedLineCount,
            reader.GetInt32(reader.GetOrdinal("blocked_grir_not_posted_line_count")),
            reader.GetInt32(reader.GetOrdinal("blocked_bill_not_posted_line_count")),
            reader.GetInt32(reader.GetOrdinal("blocked_missing_ap_open_item_line_count")),
            reader.GetInt32(reader.GetOrdinal("blocked_journal_not_posted_line_count")),
            reader.GetInt32(reader.GetOrdinal("blocked_amount_exceeded_line_count")),
            partiallySettledLineCount,
            settledLineCount,
            Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settlement_amount_base"))),
            Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("eligible_amount_base"))),
            Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("settled_amount_base"))),
            Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("remaining_amount_base"))),
            settlementBatchCount,
            journalNotPostedBatchCount,
            journalPostedBatchCount,
            journalStaleBatchCount,
            journalInconsistentBatchCount,
            ReceiptGrIrApSettlementJournalStatusPolicy.ResolveSummaryStatus(
                settlementBatchCount,
                journalNotPostedBatchCount,
                journalPostedBatchCount,
                journalStaleBatchCount,
                journalInconsistentBatchCount),
            reader.IsDBNull(reader.GetOrdinal("last_journal_refreshed_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_journal_refreshed_at")),
            openItemNotClearedBatchCount,
            openItemClearedBatchCount,
            openItemReversedBatchCount,
            openItemBlockedBatchCount,
            openItemStaleBatchCount,
            openItemInconsistentBatchCount,
            ReceiptGrIrApOpenItemClearingStatusPolicy.ResolveSummaryStatus(
                settlementBatchCount,
                openItemNotClearedBatchCount,
                openItemClearedBatchCount,
                openItemReversedBatchCount,
                openItemBlockedBatchCount,
                openItemStaleBatchCount,
                openItemInconsistentBatchCount),
            reader.IsDBNull(reader.GetOrdinal("last_open_item_cleared_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_open_item_cleared_at")),
            reader.IsDBNull(reader.GetOrdinal("last_open_item_reversed_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_open_item_reversed_at")),
            purchaseVarianceLineCount,
            purchaseVarianceCandidateLineCount,
            purchaseVarianceNoVarianceLineCount,
            purchaseVarianceBlockedLineCount,
            ReceiptGrIrApPurchaseVarianceStatusPolicy.ResolveSummaryStatus(
                purchaseVarianceLineCount,
                purchaseVarianceCandidateLineCount,
                purchaseVarianceNoVarianceLineCount,
                purchaseVarianceBlockedLineCount),
            Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("purchase_variance_amount_base"))),
            reader.IsDBNull(reader.GetOrdinal("last_purchase_variance_refreshed_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_purchase_variance_refreshed_at")),
            reader.IsDBNull(reader.GetOrdinal("last_refreshed_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_refreshed_at")),
            reader.IsDBNull(reader.GetOrdinal("last_settled_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_settled_at")));
    }

    private static string BuildSummarySql(string documentColumn, string parameterName) =>
        $"""
        with requested_documents as (
          select unnest(@{parameterName}::uuid[]) as document_id
        ),
        settlement_groups as (
          select
            line.{documentColumn} as document_id,
            count(*)::int as settlement_line_count,
            count(*) filter (where line.settlement_status in (
              '{ReceiptGrIrApSettlementStatusPolicy.EligibleNotSettled}',
              '{ReceiptGrIrApSettlementStatusPolicy.PartiallySettled}'
            ))::int as eligible_line_count,
            count(*) filter (where line.settlement_status like 'blocked_%')::int as blocked_line_count,
            count(*) filter (where line.settlement_status = '{ReceiptGrIrApSettlementStatusPolicy.BlockedGrIrNotPosted}')::int as blocked_grir_not_posted_line_count,
            count(*) filter (where line.settlement_status = '{ReceiptGrIrApSettlementStatusPolicy.BlockedBillNotPosted}')::int as blocked_bill_not_posted_line_count,
            count(*) filter (where line.settlement_status = '{ReceiptGrIrApSettlementStatusPolicy.BlockedMissingApOpenItem}')::int as blocked_missing_ap_open_item_line_count,
            count(*) filter (where line.settlement_status = '{ReceiptGrIrApSettlementStatusPolicy.BlockedJournalNotPosted}')::int as blocked_journal_not_posted_line_count,
            count(*) filter (where line.settlement_status = '{ReceiptGrIrApSettlementStatusPolicy.BlockedAmountExceeded}')::int as blocked_amount_exceeded_line_count,
            count(*) filter (where line.settlement_status = '{ReceiptGrIrApSettlementStatusPolicy.PartiallySettled}')::int as partially_settled_line_count,
            count(*) filter (where line.settlement_status = '{ReceiptGrIrApSettlementStatusPolicy.Settled}')::int as settled_line_count,
            coalesce(sum(line.settlement_amount_base), 0)::numeric(20,6) as settlement_amount_base,
            coalesce(sum(line.remaining_amount_base) filter (where line.settlement_status in (
              '{ReceiptGrIrApSettlementStatusPolicy.EligibleNotSettled}',
              '{ReceiptGrIrApSettlementStatusPolicy.PartiallySettled}'
            )), 0)::numeric(20,6) as eligible_amount_base,
            coalesce(sum(line.settled_amount_base), 0)::numeric(20,6) as settled_amount_base,
            coalesce(sum(line.remaining_amount_base), 0)::numeric(20,6) as remaining_amount_base,
            max(line.refreshed_at) as last_refreshed_at,
            max(line.last_settled_at) as last_settled_at
          from {SettlementLinesTableName} line
          where line.company_id = @company_id
            and line.{documentColumn} = any(@{parameterName})
          group by line.{documentColumn}
        ),
        batch_groups as (
          select
            line.{documentColumn} as document_id,
            count(distinct batch.id)::int as settlement_batch_count,
            count(distinct batch.id) filter (where batch.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.NotPosted}')::int as journal_not_posted_batch_count,
            count(distinct batch.id) filter (where batch.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.Posted}')::int as journal_posted_batch_count,
            count(distinct batch.id) filter (where batch.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalStale}')::int as journal_stale_batch_count,
            count(distinct batch.id) filter (where batch.journal_status = '{ReceiptGrIrApSettlementJournalStatusPolicy.JournalInconsistent}')::int as journal_inconsistent_batch_count,
            count(distinct batch.id) filter (where batch.open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.NotCleared}')::int as open_item_not_cleared_batch_count,
            count(distinct batch.id) filter (where batch.open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.Cleared}')::int as open_item_cleared_batch_count,
            count(distinct batch.id) filter (where batch.open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.Reversed}')::int as open_item_reversed_batch_count,
            count(distinct batch.id) filter (where batch.open_item_clearing_status like 'blocked_%')::int as open_item_blocked_batch_count,
            count(distinct batch.id) filter (where batch.open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.ClearingStale}')::int as open_item_stale_batch_count,
            count(distinct batch.id) filter (where batch.open_item_clearing_status = '{ReceiptGrIrApOpenItemClearingStatusPolicy.ClearingInconsistent}')::int as open_item_inconsistent_batch_count,
            max(batch.journal_refreshed_at) as last_journal_refreshed_at,
            max(batch.open_item_cleared_at) as last_open_item_cleared_at,
            max(batch.open_item_reversed_at) as last_open_item_reversed_at
          from {SettlementLinesTableName} line
          join {SettlementBatchLinesTableName} batch_line
            on batch_line.company_id = line.company_id
           and batch_line.settlement_line_id = line.id
          join {SettlementBatchesTableName} batch
            on batch.company_id = batch_line.company_id
           and batch.id = batch_line.settlement_batch_id
          where line.company_id = @company_id
            and line.{documentColumn} = any(@{parameterName})
          group by line.{documentColumn}
        ),
        variance_groups as (
          select
            variance.{documentColumn} as document_id,
            count(*)::int as purchase_variance_line_count,
            -- Field name `purchase_variance_candidate_line_count` retained
            -- for wire compatibility; the underlying status semantic is now
            -- "recognised in settlement journal" (M4) — see policy doc.
            count(*) filter (where variance.variance_status = '{ReceiptGrIrApPurchaseVarianceStatusPolicy.RecognizedInSettlement}')::int as purchase_variance_candidate_line_count,
            count(*) filter (where variance.variance_status = '{ReceiptGrIrApPurchaseVarianceStatusPolicy.NoVariance}')::int as purchase_variance_no_variance_line_count,
            count(*) filter (where variance.variance_status like 'blocked_%' or variance.variance_status = '{ReceiptGrIrApPurchaseVarianceStatusPolicy.VarianceInconsistent}')::int as purchase_variance_blocked_line_count,
            coalesce(sum(variance.variance_amount_base) filter (where variance.variance_status = '{ReceiptGrIrApPurchaseVarianceStatusPolicy.RecognizedInSettlement}'), 0)::numeric(20,6) as purchase_variance_amount_base,
            max(variance.refreshed_at) as last_purchase_variance_refreshed_at
          from {PurchaseVarianceLinesTableName} variance
          where variance.company_id = @company_id
            and variance.{documentColumn} = any(@{parameterName})
          group by variance.{documentColumn}
        )
        select
          rd.document_id,
          coalesce(sg.settlement_line_count, 0) as settlement_line_count,
          coalesce(sg.eligible_line_count, 0) as eligible_line_count,
          coalesce(sg.blocked_line_count, 0) as blocked_line_count,
          coalesce(sg.blocked_grir_not_posted_line_count, 0) as blocked_grir_not_posted_line_count,
          coalesce(sg.blocked_bill_not_posted_line_count, 0) as blocked_bill_not_posted_line_count,
          coalesce(sg.blocked_missing_ap_open_item_line_count, 0) as blocked_missing_ap_open_item_line_count,
          coalesce(sg.blocked_journal_not_posted_line_count, 0) as blocked_journal_not_posted_line_count,
          coalesce(sg.blocked_amount_exceeded_line_count, 0) as blocked_amount_exceeded_line_count,
          coalesce(sg.partially_settled_line_count, 0) as partially_settled_line_count,
          coalesce(sg.settled_line_count, 0) as settled_line_count,
          coalesce(sg.settlement_amount_base, 0)::numeric(20,6) as settlement_amount_base,
          coalesce(sg.eligible_amount_base, 0)::numeric(20,6) as eligible_amount_base,
          coalesce(sg.settled_amount_base, 0)::numeric(20,6) as settled_amount_base,
          coalesce(sg.remaining_amount_base, 0)::numeric(20,6) as remaining_amount_base,
          coalesce(bg.settlement_batch_count, 0) as settlement_batch_count,
          coalesce(bg.journal_not_posted_batch_count, 0) as journal_not_posted_batch_count,
          coalesce(bg.journal_posted_batch_count, 0) as journal_posted_batch_count,
          coalesce(bg.journal_stale_batch_count, 0) as journal_stale_batch_count,
          coalesce(bg.journal_inconsistent_batch_count, 0) as journal_inconsistent_batch_count,
          coalesce(bg.open_item_not_cleared_batch_count, 0) as open_item_not_cleared_batch_count,
          coalesce(bg.open_item_cleared_batch_count, 0) as open_item_cleared_batch_count,
          coalesce(bg.open_item_reversed_batch_count, 0) as open_item_reversed_batch_count,
          coalesce(bg.open_item_blocked_batch_count, 0) as open_item_blocked_batch_count,
          coalesce(bg.open_item_stale_batch_count, 0) as open_item_stale_batch_count,
          coalesce(bg.open_item_inconsistent_batch_count, 0) as open_item_inconsistent_batch_count,
          bg.last_journal_refreshed_at,
          bg.last_open_item_cleared_at,
          bg.last_open_item_reversed_at,
          coalesce(vg.purchase_variance_line_count, 0) as purchase_variance_line_count,
          coalesce(vg.purchase_variance_candidate_line_count, 0) as purchase_variance_candidate_line_count,
          coalesce(vg.purchase_variance_no_variance_line_count, 0) as purchase_variance_no_variance_line_count,
          coalesce(vg.purchase_variance_blocked_line_count, 0) as purchase_variance_blocked_line_count,
          coalesce(vg.purchase_variance_amount_base, 0)::numeric(20,6) as purchase_variance_amount_base,
          vg.last_purchase_variance_refreshed_at,
          sg.last_refreshed_at,
          sg.last_settled_at
        from requested_documents rd
        left join settlement_groups sg
          on sg.document_id = rd.document_id
        left join batch_groups bg
          on bg.document_id = rd.document_id
        left join variance_groups vg
          on vg.document_id = rd.document_id;
        """;

    private static async Task EnsureSchemaAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            create table if not exists {SettlementLinesTableName} (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              receipt_id uuid not null,
              receipt_line_number integer not null,
              bridge_line_id uuid not null references {BridgeLinesTableName}(id) on delete cascade,
              journal_entry_id uuid null references journal_entries(id) on delete set null,
              journal_entry_display_number text null,
              bill_id uuid not null,
              bill_line_number integer not null,
              ap_open_item_id uuid null references ap_open_items(id) on delete set null,
              item_id uuid not null,
              warehouse_id uuid not null,
              uom_code text not null,
              settlement_quantity numeric(20,6) not null,
              settlement_amount_base numeric(20,6) not null,
              settled_quantity numeric(20,6) not null default 0,
              settled_amount_base numeric(20,6) not null default 0,
              remaining_amount_base numeric(20,6) not null,
              settlement_status text not null,
              blocked_reason_code text null,
              refreshed_by_user_id uuid not null,
              refreshed_at timestamptz not null default now(),
              last_settled_at timestamptz null
            );

            create unique index if not exists ux_receipt_grir_ap_settlement_lines_bridge
              on {SettlementLinesTableName} (company_id, bridge_line_id);

            create index if not exists ix_receipt_grir_ap_settlement_lines_receipt
              on {SettlementLinesTableName} (company_id, receipt_id, settlement_status);

            create index if not exists ix_receipt_grir_ap_settlement_lines_bill
              on {SettlementLinesTableName} (company_id, bill_id, bill_line_number, settlement_status);

            create table if not exists {SettlementBatchesTableName} (
              id uuid primary key,
              company_id uuid not null references companies(id) on delete cascade,
              receipt_id uuid not null,
              idempotency_key text not null,
              status text not null,
              requested_amount_base numeric(20,6) not null,
              settled_quantity numeric(20,6) not null,
              settled_amount_base numeric(20,6) not null,
              line_count integer not null,
              created_by_user_id uuid not null,
              created_at timestamptz not null default now()
            );

            alter table {SettlementBatchesTableName}
              add column if not exists journal_status text not null default 'not_posted';

            alter table {SettlementBatchesTableName}
              add column if not exists journal_entry_id uuid null references journal_entries(id) on delete set null;

            alter table {SettlementBatchesTableName}
              add column if not exists journal_entry_display_number text null;

            alter table {SettlementBatchesTableName}
              add column if not exists journal_posted_by_user_id uuid null;

            alter table {SettlementBatchesTableName}
              add column if not exists journal_posted_at timestamptz null;

            alter table {SettlementBatchesTableName}
              add column if not exists journal_refreshed_at timestamptz null;

            alter table {SettlementBatchesTableName}
              add column if not exists journal_blocked_reason_code text null;

            alter table {SettlementBatchesTableName}
              add column if not exists open_item_clearing_status text not null default 'not_cleared';

            alter table {SettlementBatchesTableName}
              add column if not exists open_item_clearing_blocked_reason_code text null;

            alter table {SettlementBatchesTableName}
              add column if not exists open_item_cleared_by_user_id uuid null;

            alter table {SettlementBatchesTableName}
              add column if not exists open_item_cleared_at timestamptz null;

            alter table {SettlementBatchesTableName}
              add column if not exists open_item_clearing_refreshed_at timestamptz null;

            alter table {SettlementBatchesTableName}
              add column if not exists open_item_reversed_by_user_id uuid null;

            alter table {SettlementBatchesTableName}
              add column if not exists open_item_reversed_at timestamptz null;

            alter table {SettlementBatchesTableName}
              add column if not exists open_item_reversed_application_count integer not null default 0;

            alter table {SettlementBatchesTableName}
              add column if not exists open_item_reversed_amount_tx numeric(20,6) not null default 0;

            alter table {SettlementBatchesTableName}
              add column if not exists open_item_reversed_amount_base numeric(20,6) not null default 0;

            create unique index if not exists ux_receipt_grir_ap_settlement_batches_key
              on {SettlementBatchesTableName} (company_id, idempotency_key);

            create index if not exists ix_receipt_grir_ap_settlement_batches_receipt
              on {SettlementBatchesTableName} (company_id, receipt_id, created_at desc);

            create table if not exists {SettlementBatchLinesTableName} (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              settlement_batch_id uuid not null references {SettlementBatchesTableName}(id) on delete cascade,
              settlement_line_id uuid not null references {SettlementLinesTableName}(id) on delete restrict,
              bridge_line_id uuid not null references {BridgeLinesTableName}(id) on delete restrict,
              bill_id uuid not null,
              ap_open_item_id uuid null references ap_open_items(id) on delete set null,
              settled_quantity numeric(20,6) not null,
              settled_amount_base numeric(20,6) not null,
              created_at timestamptz not null default now()
            );

            create unique index if not exists ux_receipt_grir_ap_settlement_batch_lines_line
              on {SettlementBatchLinesTableName} (company_id, settlement_batch_id, settlement_line_id);

            create index if not exists ix_receipt_grir_ap_settlement_batch_lines_bridge
              on {SettlementBatchLinesTableName} (company_id, bridge_line_id);

            create table if not exists {PurchaseVarianceLinesTableName} (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              receipt_id uuid not null,
              receipt_line_number integer not null,
              settlement_batch_id uuid not null references {SettlementBatchesTableName}(id) on delete cascade,
              settlement_batch_line_id uuid not null references {SettlementBatchLinesTableName}(id) on delete cascade,
              settlement_line_id uuid not null references {SettlementLinesTableName}(id) on delete cascade,
              bridge_line_id uuid not null references {BridgeLinesTableName}(id) on delete cascade,
              bill_id uuid not null,
              bill_line_number integer not null,
              item_id uuid not null,
              warehouse_id uuid not null,
              uom_code text not null,
              settled_quantity numeric(20,6) not null,
              grir_amount_base numeric(20,6) not null,
              bill_amount_base numeric(20,6) not null,
              variance_amount_base numeric(20,6) not null,
              variance_status text not null,
              blocked_reason_code text null,
              refreshed_by_user_id uuid not null,
              refreshed_at timestamptz not null default now()
            );

            create unique index if not exists ux_receipt_grir_ap_purchase_variance_batch_line
              on {PurchaseVarianceLinesTableName} (company_id, settlement_batch_line_id);

            create index if not exists ix_receipt_grir_ap_purchase_variance_receipt
              on {PurchaseVarianceLinesTableName} (company_id, receipt_id, variance_status);

            create index if not exists ix_receipt_grir_ap_purchase_variance_bill
              on {PurchaseVarianceLinesTableName} (company_id, bill_id, bill_line_number, variance_status);
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        PostgresCommandScope scope,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand("select to_regclass(@table_name) is not null;");
        command.Parameters.AddWithValue("table_name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static ReceiptGrIrApSettlementSummary BuildEmptyReceiptSummary(Guid receiptDocumentId) =>
        new(
            receiptDocumentId,
            ReceiptGrIrApSettlementStatusPolicy.NotEligible,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0m,
            0m,
            0m,
            0m,
            0,
            0,
            0,
            0,
            0,
            ReceiptGrIrApSettlementJournalStatusPolicy.NotApplicable,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            ReceiptGrIrApOpenItemClearingStatusPolicy.NotApplicable,
            null,
            null,
            0,
            0,
            0,
            0,
            ReceiptGrIrApPurchaseVarianceStatusPolicy.NotApplicable,
            0m,
            null,
            null,
            null);

    private static BillGrIrApSettlementSummary BuildEmptyBillSummary(Guid billDocumentId) =>
        new(
            billDocumentId,
            ReceiptGrIrApSettlementStatusPolicy.NotEligible,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0m,
            0m,
            0m,
            0m,
            0,
            0,
            0,
            0,
            0,
            ReceiptGrIrApSettlementJournalStatusPolicy.NotApplicable,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            ReceiptGrIrApOpenItemClearingStatusPolicy.NotApplicable,
            null,
            null,
            0,
            0,
            0,
            0,
            ReceiptGrIrApPurchaseVarianceStatusPolicy.NotApplicable,
            0m,
            null,
            null,
            null);

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private sealed record ExecutableSettlementLine(
        Guid SettlementLineId,
        Guid BridgeLineId,
        Guid BillId,
        Guid? ApOpenItemId,
        decimal SettlementQuantity,
        decimal SettlementAmountBase,
        decimal SettledQuantity,
        decimal SettledAmountBase,
        decimal RemainingAmountBase);

    private sealed record SettlementAllocation(
        Guid SettlementLineId,
        Guid BridgeLineId,
        Guid BillId,
        Guid? ApOpenItemId,
        decimal SettledQuantity,
        decimal SettledAmountBase);

    private sealed record OpenItemClearingBatchHeader(
        string BatchStatus,
        string JournalStatus,
        string OpenItemClearingStatus);

    private sealed record OpenItemClearingAllocation(
        Guid ApOpenItemId,
        Guid VendorId,
        string DocumentCurrencyCode,
        string BaseCurrencyCode,
        decimal OpenAmountTx,
        decimal OpenAmountBase,
        string Status,
        decimal AmountBase);

    private sealed record OpenItemClearingApplication(
        Guid ApplicationId,
        Guid ApOpenItemId,
        decimal AppliedAmountTx,
        decimal AppliedAmountBase);
}
