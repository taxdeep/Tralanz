using Citus.Accounting.Application;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresBillReceiptMatchingRepository : IBillReceiptMatchingRepository
{
    private const string AllocationsTableName = "bill_receipt_matching_allocations";
    private const string DiscrepanciesTableName = "bill_receipt_matching_discrepancy_lanes";
    private const string ReceiptsTableName = "receipts";
    private const string ReceiptLinesTableName = "receipt_lines";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresBillReceiptMatchingRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<BillReceiptMatchingLaneSummary> GetBillLaneSummaryAsync(
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (scope.Transaction is not null)
        {
            await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);
            await RefreshForBillsAsync(scope.Connection, scope.Transaction, companyId, new[] { billDocumentId }, cancellationToken);
            return await LoadBillLaneSummaryAsync(scope.Connection, scope.Transaction, companyId, billDocumentId, cancellationToken);
        }

        await using var transaction = await scope.Connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(scope.Connection, transaction, cancellationToken);
        await RefreshForBillsAsync(scope.Connection, transaction, companyId, new[] { billDocumentId }, cancellationToken);
        var summary = await LoadBillLaneSummaryAsync(scope.Connection, transaction, companyId, billDocumentId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return summary;
    }

    public async Task<IReadOnlyDictionary<Guid, BillReceiptPostingGateSnapshot>> GetBillPostingGateSnapshotsAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> billDocumentIds,
        CancellationToken cancellationToken)
    {
        if (billDocumentIds.Count == 0)
        {
            return new Dictionary<Guid, BillReceiptPostingGateSnapshot>();
        }

        var requestedBillIds = billDocumentIds.Distinct().ToArray();

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        if (scope.Transaction is not null)
        {
            await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);
            await RefreshForBillsAsync(scope.Connection, scope.Transaction, companyId, requestedBillIds, cancellationToken);
            return await LoadPostingGateSnapshotsAsync(scope.Connection, scope.Transaction, companyId, requestedBillIds, cancellationToken);
        }

        await using var transaction = await scope.Connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(scope.Connection, transaction, cancellationToken);
        await RefreshForBillsAsync(scope.Connection, transaction, companyId, requestedBillIds, cancellationToken);
        var snapshots = await LoadPostingGateSnapshotsAsync(scope.Connection, transaction, companyId, requestedBillIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshots;
    }

    private static async Task<BillReceiptMatchingLaneSummary> LoadBillLaneSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var headerCommand = connection.CreateCommand();
        headerCommand.Transaction = transaction;
        headerCommand.CommandText =
            $"""
            with requested_bill as (
              select @bill_document_id::uuid as bill_document_id
            ),
            bill_groups as (
              select
                l.bill_id as bill_document_id,
                count(*)::int as bill_inbound_line_count,
                coalesce(sum(l.quantity), 0)::numeric(18,6) as bill_inbound_quantity
              from bill_lines l
              where l.company_id = @company_id
                and l.bill_id = @bill_document_id
                and l.item_id is not null
                and l.warehouse_id is not null
                and l.uom_code is not null
                and l.quantity is not null
                and l.unit_cost is not null
              group by l.bill_id
            ),
            allocation_groups as (
              select
                a.bill_id as bill_document_id,
                count(distinct a.receipt_id)::int as receipt_count,
                coalesce(sum(a.matched_quantity), 0)::numeric(18,6) as covered_quantity,
                max(r.posted_at) as latest_receipt_posted_at
              from {AllocationsTableName} a
              join {ReceiptsTableName} r
                on r.company_id = a.company_id
               and r.id = a.receipt_id
              where a.company_id = @company_id
                and a.bill_id = @bill_document_id
              group by a.bill_id
            ),
            discrepancy_groups as (
              select
                d.bill_id as bill_document_id,
                count(*)::int as open_discrepancy_count
              from {DiscrepanciesTableName} d
              where d.company_id = @company_id
                and d.bill_id = @bill_document_id
              group by d.bill_id
            )
            select
              rb.bill_document_id,
              coalesce(bg.bill_inbound_line_count, 0) as bill_inbound_line_count,
              coalesce(bg.bill_inbound_quantity, 0)::numeric(18,6) as bill_inbound_quantity,
              coalesce(ag.receipt_count, 0) as receipt_count,
              coalesce(ag.covered_quantity, 0)::numeric(18,6) as covered_quantity,
              ag.latest_receipt_posted_at,
              coalesce(dg.open_discrepancy_count, 0) as open_discrepancy_count
            from requested_bill rb
            left join bill_groups bg
              on bg.bill_document_id = rb.bill_document_id
            left join allocation_groups ag
              on ag.bill_document_id = rb.bill_document_id
            left join discrepancy_groups dg
              on dg.bill_document_id = rb.bill_document_id;
            """;
        headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
        headerCommand.Parameters.AddWithValue("bill_document_id", billDocumentId);

        int billInboundLineCount;
        decimal billInboundQuantity;
        int receiptCount;
        decimal coveredQuantity;
        DateTimeOffset? latestReceiptPostedAt;
        int openDiscrepancyCount;

        await using (var reader = await headerCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Bill receipt matching summary could not be loaded.");
            }

            billInboundLineCount = reader.GetInt32(reader.GetOrdinal("bill_inbound_line_count"));
            billInboundQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("bill_inbound_quantity")));
            receiptCount = reader.GetInt32(reader.GetOrdinal("receipt_count"));
            coveredQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("covered_quantity")));
            latestReceiptPostedAt = reader.IsDBNull(reader.GetOrdinal("latest_receipt_posted_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_receipt_posted_at"));
            openDiscrepancyCount = reader.GetInt32(reader.GetOrdinal("open_discrepancy_count"));
        }

        var remainingQuantity = BillReceiptMatchingPolicy.ResolveRemainingQuantity(billInboundQuantity, coveredQuantity);
        var matchStatus = ResolveOverallMatchStatus(billInboundLineCount, billInboundQuantity, coveredQuantity);
        var recentReceipts = await LoadMatchedReceiptsAsync(connection, transaction, companyId, billDocumentId, cancellationToken);
        var lineSummaries = await LoadLineSummariesAsync(connection, transaction, companyId, billDocumentId, cancellationToken);
        var discrepancies = await LoadDiscrepanciesAsync(connection, transaction, companyId, billDocumentId, cancellationToken);

        return new BillReceiptMatchingLaneSummary(
            billDocumentId,
            billInboundLineCount,
            billInboundQuantity,
            receiptCount,
            coveredQuantity,
            remainingQuantity,
            matchStatus,
            latestReceiptPostedAt,
            recentReceipts,
            lineSummaries,
            discrepancies);
    }

    private static async Task<IReadOnlyDictionary<Guid, BillReceiptPostingGateSnapshot>> LoadPostingGateSnapshotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid[] billDocumentIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with requested_bills as (
              select unnest(@bill_document_ids::uuid[]) as bill_document_id
            ),
            bill_groups as (
              select
                l.bill_id as bill_document_id,
                count(*)::int as bill_inbound_line_count,
                coalesce(sum(l.quantity), 0)::numeric(18,6) as bill_inbound_quantity
              from bill_lines l
              where l.company_id = @company_id
                and l.bill_id = any(@bill_document_ids)
                and l.item_id is not null
                and l.warehouse_id is not null
                and l.uom_code is not null
                and l.quantity is not null
                and l.unit_cost is not null
              group by l.bill_id
            ),
            allocation_groups as (
              select
                a.bill_id as bill_document_id,
                count(distinct a.receipt_id)::int as receipt_count,
                coalesce(sum(a.matched_quantity), 0)::numeric(18,6) as covered_quantity,
                max(r.posted_at) as latest_receipt_posted_at
              from {AllocationsTableName} a
              join {ReceiptsTableName} r
                on r.company_id = a.company_id
               and r.id = a.receipt_id
              where a.company_id = @company_id
                and a.bill_id = any(@bill_document_ids)
              group by a.bill_id
            ),
            discrepancy_groups as (
              select
                d.bill_id as bill_document_id,
                count(*)::int as open_discrepancy_count
              from {DiscrepanciesTableName} d
              where d.company_id = @company_id
                and d.bill_id = any(@bill_document_ids)
              group by d.bill_id
            )
            select
              rb.bill_document_id,
              coalesce(bg.bill_inbound_line_count, 0) as bill_inbound_line_count,
              coalesce(bg.bill_inbound_quantity, 0)::numeric(18,6) as bill_inbound_quantity,
              coalesce(ag.receipt_count, 0) as receipt_count,
              coalesce(ag.covered_quantity, 0)::numeric(18,6) as covered_quantity,
              ag.latest_receipt_posted_at,
              coalesce(dg.open_discrepancy_count, 0) as open_discrepancy_count
            from requested_bills rb
            left join bill_groups bg
              on bg.bill_document_id = rb.bill_document_id
            left join allocation_groups ag
              on ag.bill_document_id = rb.bill_document_id
            left join discrepancy_groups dg
              on dg.bill_document_id = rb.bill_document_id;
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("bill_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = billDocumentIds
        });
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var snapshots = new Dictionary<Guid, BillReceiptPostingGateSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var billDocumentId = reader.GetGuid(reader.GetOrdinal("bill_document_id"));
            var billInboundLineCount = reader.GetInt32(reader.GetOrdinal("bill_inbound_line_count"));
            var billInboundQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("bill_inbound_quantity")));
            var receiptCount = reader.GetInt32(reader.GetOrdinal("receipt_count"));
            var coveredQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("covered_quantity")));
            var remainingQuantity = BillReceiptMatchingPolicy.ResolveRemainingQuantity(billInboundQuantity, coveredQuantity);
            var matchStatus = ResolveOverallMatchStatus(billInboundLineCount, billInboundQuantity, coveredQuantity);
            var openDiscrepancyCount = reader.GetInt32(reader.GetOrdinal("open_discrepancy_count"));

            snapshots[billDocumentId] = new BillReceiptPostingGateSnapshot(
                billDocumentId,
                billInboundLineCount,
                billInboundQuantity,
                receiptCount,
                coveredQuantity,
                remainingQuantity,
                matchStatus,
                reader.IsDBNull(reader.GetOrdinal("latest_receipt_posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_receipt_posted_at")),
                openDiscrepancyCount);
        }

        return snapshots;
    }

    private static async Task<IReadOnlyList<BillReceiptMatchingReceiptSummary>> LoadMatchedReceiptsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with receipt_totals as (
              select
                l.receipt_id,
                coalesce(sum(l.quantity), 0)::numeric(18,6) as receipt_quantity
              from {ReceiptLinesTableName} l
              where l.company_id = @company_id
              group by l.receipt_id
            )
            select
              r.id as receipt_document_id,
              r.receipt_number as display_number,
              r.receipt_date,
              r.status,
              coalesce(rt.receipt_quantity, 0)::numeric(18,6) as receipt_quantity,
              coalesce(sum(a.matched_quantity), 0)::numeric(18,6) as matched_quantity,
              r.vendor_reference,
              r.source_reference,
              r.posted_at
            from {AllocationsTableName} a
            join {ReceiptsTableName} r
              on r.company_id = a.company_id
             and r.id = a.receipt_id
            left join receipt_totals rt
              on rt.receipt_id = r.id
            where a.company_id = @company_id
              and a.bill_id = @bill_document_id
            group by
              r.id,
              r.receipt_number,
              r.receipt_date,
              r.status,
              rt.receipt_quantity,
              r.vendor_reference,
              r.source_reference,
              r.posted_at
            order by r.receipt_date desc, r.posted_at desc nulls last, r.receipt_number desc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bill_document_id", billDocumentId);

        var receipts = new List<BillReceiptMatchingReceiptSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            receipts.Add(new BillReceiptMatchingReceiptSummary(
                reader.GetGuid(reader.GetOrdinal("receipt_document_id")),
                reader.GetString(reader.GetOrdinal("display_number")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("receipt_date")),
                reader.GetString(reader.GetOrdinal("status")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("receipt_quantity"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("matched_quantity"))),
                reader.IsDBNull(reader.GetOrdinal("vendor_reference")) ? null : reader.GetString(reader.GetOrdinal("vendor_reference")),
                reader.IsDBNull(reader.GetOrdinal("source_reference")) ? null : reader.GetString(reader.GetOrdinal("source_reference")),
                reader.IsDBNull(reader.GetOrdinal("posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"))));
        }

        return receipts;
    }

    private static async Task<IReadOnlyList<BillReceiptMatchingLineSummary>> LoadLineSummariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with inventory_bill_lines as (
              select
                l.line_number,
                l.item_id,
                coalesce(i.item_code, 'UNKNOWN') as item_code,
                coalesce(i.name, 'Unknown item') as item_name,
                l.warehouse_id,
                coalesce(w.warehouse_code, 'UNKNOWN') as warehouse_code,
                coalesce(w.name, 'Unknown warehouse') as warehouse_name,
                upper(trim(l.uom_code)) as uom_code,
                l.quantity::numeric(18,6) as bill_quantity
              from bill_lines l
              left join inventory_items i
                on i.company_id = l.company_id
               and i.id = l.item_id
              left join inventory_warehouses w
                on w.company_id = l.company_id
               and w.id = l.warehouse_id
              where l.company_id = @company_id
                and l.bill_id = @bill_document_id
                and l.item_id is not null
                and l.warehouse_id is not null
                and l.uom_code is not null
                and l.quantity is not null
                and l.unit_cost is not null
            ),
            allocation_groups as (
              select
                a.bill_line_number,
                count(distinct a.receipt_id)::int as receipt_count,
                coalesce(sum(a.matched_quantity), 0)::numeric(18,6) as covered_quantity
              from {AllocationsTableName} a
              where a.company_id = @company_id
                and a.bill_id = @bill_document_id
              group by a.bill_line_number
            )
            select
              l.line_number,
              l.item_id,
              l.item_code,
              l.item_name,
              l.warehouse_id,
              l.warehouse_code,
              l.warehouse_name,
              l.uom_code,
              l.bill_quantity,
              coalesce(a.covered_quantity, 0)::numeric(18,6) as covered_quantity,
              coalesce(a.receipt_count, 0) as receipt_count
            from inventory_bill_lines l
            left join allocation_groups a
              on a.bill_line_number = l.line_number
            order by l.line_number asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bill_document_id", billDocumentId);

        var rows = new List<BillReceiptMatchingLineSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var billQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("bill_quantity")));
            var coveredQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("covered_quantity")));
            var remainingQuantity = BillReceiptMatchingPolicy.ResolveRemainingQuantity(billQuantity, coveredQuantity);
            var matchStatus = BillReceiptMatchingPolicy.ResolveCoverageStatus(billQuantity, coveredQuantity);

            rows.Add(new BillReceiptMatchingLineSummary(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                billQuantity,
                coveredQuantity,
                remainingQuantity,
                reader.GetInt32(reader.GetOrdinal("receipt_count")),
                matchStatus));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<BillReceiptMatchingDiscrepancySummary>> LoadDiscrepanciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select
              d.bill_id,
              d.bill_line_number,
              d.discrepancy_type,
              d.investigation_status,
              d.item_id,
              coalesce(i.item_code, 'UNKNOWN') as item_code,
              coalesce(i.name, 'Unknown item') as item_name,
              d.warehouse_id,
              coalesce(w.warehouse_code, 'UNKNOWN') as warehouse_code,
              coalesce(w.name, 'Unknown warehouse') as warehouse_name,
              d.uom_code,
              d.bill_quantity,
              d.covered_quantity,
              d.remaining_quantity,
              d.summary,
              d.first_detected_at,
              d.last_detected_at
            from {DiscrepanciesTableName} d
            left join inventory_items i
              on i.company_id = d.company_id
             and i.id = d.item_id
            left join inventory_warehouses w
              on w.company_id = d.company_id
             and w.id = d.warehouse_id
            where d.company_id = @company_id
              and d.bill_id = @bill_document_id
            order by d.bill_line_number asc, d.last_detected_at desc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bill_document_id", billDocumentId);

        var rows = new List<BillReceiptMatchingDiscrepancySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BillReceiptMatchingDiscrepancySummary(
                reader.GetGuid(reader.GetOrdinal("bill_id")),
                reader.GetInt32(reader.GetOrdinal("bill_line_number")),
                reader.GetString(reader.GetOrdinal("discrepancy_type")),
                reader.GetString(reader.GetOrdinal("investigation_status")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("bill_quantity"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("covered_quantity"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("remaining_quantity"))),
                reader.GetString(reader.GetOrdinal("summary")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("first_detected_at")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_detected_at"))));
        }

        return rows;
    }

    private static async Task RefreshForBillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid[] billDocumentIds,
        CancellationToken cancellationToken)
    {
        if (billDocumentIds.Length == 0)
        {
            return;
        }

        var currentGroups = await LoadCurrentBillGroupsAsync(connection, transaction, companyId, billDocumentIds, cancellationToken);
        var staleGroups = await LoadAllocatedBillGroupsAsync(connection, transaction, companyId, billDocumentIds, cancellationToken);
        var touchedGroups = currentGroups
            .Concat(staleGroups)
            .Distinct()
            .ToArray();

        if (touchedGroups.Length == 0)
        {
            await DeleteAllocationsForBillsAsync(connection, transaction, companyId, billDocumentIds, cancellationToken);
            await DeleteDiscrepanciesForBillsAsync(connection, transaction, companyId, billDocumentIds, cancellationToken);
            return;
        }

        await DeleteAllocationsForGroupsAsync(connection, transaction, companyId, touchedGroups, cancellationToken);

        var billCandidates = await LoadBillCandidatesAsync(connection, transaction, companyId, touchedGroups, cancellationToken);
        if (billCandidates.Count == 0)
        {
            await DeleteDiscrepanciesForBillsAsync(connection, transaction, companyId, billDocumentIds, cancellationToken);
            return;
        }

        var receiptCandidates = await LoadReceiptCandidatesAsync(connection, transaction, companyId, touchedGroups, cancellationToken);
        if (receiptCandidates.Count == 0)
        {
            await RefreshDiscrepanciesForBillsAsync(connection, transaction, companyId, billDocumentIds, cancellationToken);
            return;
        }

        var computation = BillReceiptMatchingPolicy.Compute(billCandidates, receiptCandidates);
        if (computation.Allocations.Count > 0)
        {
            await InsertAllocationsAsync(connection, transaction, companyId, computation.Allocations, cancellationToken);
        }

        await RefreshDiscrepanciesForBillsAsync(connection, transaction, companyId, billDocumentIds, cancellationToken);
    }

    private static async Task<IReadOnlyList<BillReceiptMatchingAnchor>> LoadCurrentBillGroupsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid[] billDocumentIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select distinct
              b.vendor_id,
              l.item_id,
              l.warehouse_id,
              upper(trim(l.uom_code)) as uom_code
            from bills b
            join bill_lines l
              on l.company_id = b.company_id
             and l.bill_id = b.id
            where b.company_id = @company_id
              and b.id = any(@bill_document_ids)
              and l.item_id is not null
              and l.warehouse_id is not null
              and l.uom_code is not null
              and l.quantity is not null
              and l.unit_cost is not null;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("bill_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = billDocumentIds
        });

        return await ReadGroupsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<BillReceiptMatchingAnchor>> LoadAllocatedBillGroupsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid[] billDocumentIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select distinct
              vendor_id,
              item_id,
              warehouse_id,
              upper(trim(uom_code)) as uom_code
            from {AllocationsTableName}
            where company_id = @company_id
              and bill_id = any(@bill_document_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("bill_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = billDocumentIds
        });

        return await ReadGroupsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<BillReceiptMatchingAnchor>> ReadGroupsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var groups = new List<BillReceiptMatchingAnchor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(new BillReceiptMatchingAnchor(
                reader.GetGuid(reader.GetOrdinal("vendor_id")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("uom_code"))));
        }

        return groups;
    }

    private static async Task DeleteAllocationsForBillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid[] billDocumentIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            delete from {AllocationsTableName}
            where company_id = @company_id
              and bill_id = any(@bill_document_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("bill_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = billDocumentIds
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteDiscrepanciesForBillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid[] billDocumentIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            delete from {DiscrepanciesTableName}
            where company_id = @company_id
              and bill_id = any(@bill_document_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("bill_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = billDocumentIds
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteAllocationsForGroupsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        IReadOnlyList<BillReceiptMatchingAnchor> groups,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with matching_groups as (
              select *
              from unnest(
                @vendor_ids::uuid[],
                @item_ids::uuid[],
                @warehouse_ids::uuid[],
                @uom_codes::text[]
              ) as g(vendor_id, item_id, warehouse_id, uom_code)
            )
            delete from {AllocationsTableName} allocation
            using matching_groups g
            where allocation.company_id = @company_id
              and allocation.vendor_id = g.vendor_id
              and allocation.item_id = g.item_id
              and allocation.warehouse_id = g.warehouse_id
              and allocation.uom_code = g.uom_code;
            """;
        BindGroups(command, groups);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<BillReceiptMatchBillLineCandidate>> LoadBillCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        IReadOnlyList<BillReceiptMatchingAnchor> groups,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            with matching_groups as (
              select *
              from unnest(
                @vendor_ids::uuid[],
                @item_ids::uuid[],
                @warehouse_ids::uuid[],
                @uom_codes::text[]
              ) as g(vendor_id, item_id, warehouse_id, uom_code)
            )
            select
              b.id as bill_id,
              b.status as bill_status,
              l.line_number,
              b.bill_date,
              b.created_at as bill_created_at,
              b.vendor_id,
              l.item_id,
              l.warehouse_id,
              upper(trim(l.uom_code)) as uom_code,
              l.quantity::numeric(18,6) as quantity
            from bills b
            join bill_lines l
              on l.company_id = b.company_id
             and l.bill_id = b.id
            join matching_groups g
              on g.vendor_id = b.vendor_id
             and g.item_id = l.item_id
             and g.warehouse_id = l.warehouse_id
             and g.uom_code = upper(trim(l.uom_code))
            where b.company_id = @company_id
              and b.status in ('draft', 'submitted', 'posted')
              and l.item_id is not null
              and l.warehouse_id is not null
              and l.uom_code is not null
              and l.quantity is not null
              and l.unit_cost is not null;
            """;
        BindGroups(command, groups);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var candidates = new List<BillReceiptMatchBillLineCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new BillReceiptMatchBillLineCandidate(
                reader.GetGuid(reader.GetOrdinal("bill_id")),
                reader.GetString(reader.GetOrdinal("bill_status")),
                reader.GetInt32(reader.GetOrdinal("line_number")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("bill_date")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("bill_created_at")),
                reader.GetGuid(reader.GetOrdinal("vendor_id")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("quantity")))));
        }

        return candidates;
    }

    private static async Task<IReadOnlyList<BillReceiptMatchReceiptLineCandidate>> LoadReceiptCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        IReadOnlyList<BillReceiptMatchingAnchor> groups,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with matching_groups as (
              select *
              from unnest(
                @vendor_ids::uuid[],
                @item_ids::uuid[],
                @warehouse_ids::uuid[],
                @uom_codes::text[]
              ) as g(vendor_id, item_id, warehouse_id, uom_code)
            )
            select
              r.id as receipt_id,
              l.line_number,
              r.receipt_date,
              r.created_at as receipt_created_at,
              r.vendor_id,
              l.item_id,
              r.warehouse_id,
              upper(trim(l.uom_code)) as uom_code,
              l.quantity::numeric(18,6) as quantity
            from {ReceiptsTableName} r
            join {ReceiptLinesTableName} l
              on l.company_id = r.company_id
             and l.receipt_id = r.id
            join matching_groups g
              on g.vendor_id = r.vendor_id
             and g.item_id = l.item_id
             and g.warehouse_id = r.warehouse_id
             and g.uom_code = upper(trim(l.uom_code))
            where r.company_id = @company_id
              and r.status = 'posted';
            """;
        BindGroups(command, groups);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var candidates = new List<BillReceiptMatchReceiptLineCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new BillReceiptMatchReceiptLineCandidate(
                reader.GetGuid(reader.GetOrdinal("receipt_id")),
                reader.GetInt32(reader.GetOrdinal("line_number")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("receipt_date")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("receipt_created_at")),
                reader.GetGuid(reader.GetOrdinal("vendor_id")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("quantity")))));
        }

        return candidates;
    }

    private static async Task InsertAllocationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        IReadOnlyList<BillReceiptMatchAllocation> allocations,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            insert into {AllocationsTableName} (
              id,
              company_id,
              vendor_id,
              item_id,
              warehouse_id,
              uom_code,
              bill_id,
              bill_line_number,
              receipt_id,
              receipt_line_number,
              matched_quantity,
              created_at
            )
            values (
              @id,
              @company_id,
              @vendor_id,
              @item_id,
              @warehouse_id,
              @uom_code,
              @bill_id,
              @bill_line_number,
              @receipt_id,
              @receipt_line_number,
              @matched_quantity,
              now()
            );
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter<Guid>("company_id", NpgsqlDbType.Uuid) { TypedValue = companyId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("vendor_id", NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter<Guid>("item_id", NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter<Guid>("warehouse_id", NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter<string>("uom_code", NpgsqlDbType.Text));
        command.Parameters.Add(new NpgsqlParameter<Guid>("bill_id", NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter<int>("bill_line_number", NpgsqlDbType.Integer));
        command.Parameters.Add(new NpgsqlParameter<Guid>("receipt_id", NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter<int>("receipt_line_number", NpgsqlDbType.Integer));
        command.Parameters.Add(new NpgsqlParameter<decimal>("matched_quantity", NpgsqlDbType.Numeric));

        foreach (var allocation in allocations)
        {
            command.Parameters["id"].Value = Guid.NewGuid();
            command.Parameters["vendor_id"].Value = allocation.Anchor.VendorId;
            command.Parameters["item_id"].Value = allocation.Anchor.ItemId;
            command.Parameters["warehouse_id"].Value = allocation.Anchor.WarehouseId;
            command.Parameters["uom_code"].Value = allocation.Anchor.UomCode;
            command.Parameters["bill_id"].Value = allocation.BillId;
            command.Parameters["bill_line_number"].Value = allocation.BillLineNumber;
            command.Parameters["receipt_id"].Value = allocation.ReceiptId;
            command.Parameters["receipt_line_number"].Value = allocation.ReceiptLineNumber;
            command.Parameters["matched_quantity"].Value = Round6(allocation.MatchedQuantity);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task RefreshDiscrepanciesForBillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid[] billDocumentIds,
        CancellationToken cancellationToken)
    {
        var existingDiscrepancies = await LoadExistingDiscrepancyFirstDetectedAtAsync(
            connection,
            transaction,
            companyId,
            billDocumentIds,
            cancellationToken);

        await DeleteDiscrepanciesForBillsAsync(connection, transaction, companyId, billDocumentIds, cancellationToken);

        var candidates = await LoadDiscrepancyCandidatesAsync(
            connection,
            transaction,
            companyId,
            billDocumentIds,
            cancellationToken);

        if (candidates.Count == 0)
        {
            return;
        }

        await InsertDiscrepanciesAsync(
            connection,
            transaction,
            companyId,
            candidates,
            existingDiscrepancies,
            cancellationToken);
    }

    private static async Task<Dictionary<(Guid BillId, int BillLineNumber, string DiscrepancyType), DateTimeOffset>> LoadExistingDiscrepancyFirstDetectedAtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid[] billDocumentIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select
              bill_id,
              bill_line_number,
              discrepancy_type,
              first_detected_at
            from {DiscrepanciesTableName}
            where company_id = @company_id
              and bill_id = any(@bill_document_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("bill_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = billDocumentIds
        });

        var rows = new Dictionary<(Guid BillId, int BillLineNumber, string DiscrepancyType), DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[(reader.GetGuid(reader.GetOrdinal("bill_id")), reader.GetInt32(reader.GetOrdinal("bill_line_number")), reader.GetString(reader.GetOrdinal("discrepancy_type")))] =
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("first_detected_at"));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<BillReceiptMatchingDiscrepancySummary>> LoadDiscrepancyCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid[] billDocumentIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with inventory_bill_lines as (
              select
                l.bill_id,
                l.line_number,
                l.item_id,
                coalesce(i.item_code, 'UNKNOWN') as item_code,
                coalesce(i.name, 'Unknown item') as item_name,
                l.warehouse_id,
                coalesce(w.warehouse_code, 'UNKNOWN') as warehouse_code,
                coalesce(w.name, 'Unknown warehouse') as warehouse_name,
                upper(trim(l.uom_code)) as uom_code,
                l.quantity::numeric(18,6) as bill_quantity
              from bill_lines l
              left join inventory_items i
                on i.company_id = l.company_id
               and i.id = l.item_id
              left join inventory_warehouses w
                on w.company_id = l.company_id
               and w.id = l.warehouse_id
              where l.company_id = @company_id
                and l.bill_id = any(@bill_document_ids)
                and l.item_id is not null
                and l.warehouse_id is not null
                and l.uom_code is not null
                and l.quantity is not null
                and l.unit_cost is not null
            ),
            allocation_groups as (
              select
                a.bill_id,
                a.bill_line_number,
                coalesce(sum(a.matched_quantity), 0)::numeric(18,6) as covered_quantity
              from {AllocationsTableName} a
              where a.company_id = @company_id
                and a.bill_id = any(@bill_document_ids)
              group by a.bill_id, a.bill_line_number
            )
            select
              l.bill_id,
              l.line_number,
              l.item_id,
              l.item_code,
              l.item_name,
              l.warehouse_id,
              l.warehouse_code,
              l.warehouse_name,
              l.uom_code,
              l.bill_quantity,
              coalesce(a.covered_quantity, 0)::numeric(18,6) as covered_quantity
            from inventory_bill_lines l
            left join allocation_groups a
              on a.bill_id = l.bill_id
             and a.bill_line_number = l.line_number
            order by l.bill_id asc, l.line_number asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("bill_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = billDocumentIds
        });

        var rows = new List<BillReceiptMatchingDiscrepancySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var billQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("bill_quantity")));
            var coveredQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("covered_quantity")));
            var remainingQuantity = BillReceiptMatchingPolicy.ResolveRemainingQuantity(billQuantity, coveredQuantity);
            var matchStatus = BillReceiptMatchingPolicy.ResolveCoverageStatus(billQuantity, coveredQuantity);
            var discrepancyType = BillReceiptDiscrepancyPolicy.ResolveDiscrepancyType(matchStatus);
            if (discrepancyType is null)
            {
                continue;
            }

            rows.Add(new BillReceiptMatchingDiscrepancySummary(
                reader.GetGuid(reader.GetOrdinal("bill_id")),
                reader.GetInt32(reader.GetOrdinal("line_number")),
                discrepancyType,
                BillReceiptDiscrepancyPolicy.ResolveInvestigationStatus(discrepancyType),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                billQuantity,
                coveredQuantity,
                remainingQuantity,
                BillReceiptDiscrepancyPolicy.BuildDiscrepancySummary(
                    discrepancyType,
                    reader.GetString(reader.GetOrdinal("item_code")),
                    reader.GetString(reader.GetOrdinal("warehouse_code")),
                    remainingQuantity,
                    reader.GetString(reader.GetOrdinal("uom_code"))),
                default,
                default));
        }

        return rows;
    }

    private static async Task InsertDiscrepanciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        IReadOnlyList<BillReceiptMatchingDiscrepancySummary> discrepancies,
        IReadOnlyDictionary<(Guid BillId, int BillLineNumber, string DiscrepancyType), DateTimeOffset> existingDiscrepancies,
        CancellationToken cancellationToken)
    {
        var detectedAt = DateTimeOffset.UtcNow;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            insert into {DiscrepanciesTableName} (
              id,
              company_id,
              bill_id,
              bill_line_number,
              discrepancy_type,
              investigation_status,
              item_id,
              warehouse_id,
              uom_code,
              bill_quantity,
              covered_quantity,
              remaining_quantity,
              summary,
              first_detected_at,
              last_detected_at
            )
            values (
              @id,
              @company_id,
              @bill_id,
              @bill_line_number,
              @discrepancy_type,
              @investigation_status,
              @item_id,
              @warehouse_id,
              @uom_code,
              @bill_quantity,
              @covered_quantity,
              @remaining_quantity,
              @summary,
              @first_detected_at,
              @last_detected_at
            );
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter<Guid>("company_id", NpgsqlDbType.Uuid) { TypedValue = companyId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("bill_id", NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter<int>("bill_line_number", NpgsqlDbType.Integer));
        command.Parameters.Add(new NpgsqlParameter<string>("discrepancy_type", NpgsqlDbType.Text));
        command.Parameters.Add(new NpgsqlParameter<string>("investigation_status", NpgsqlDbType.Text));
        command.Parameters.Add(new NpgsqlParameter<Guid>("item_id", NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter<Guid>("warehouse_id", NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter<string>("uom_code", NpgsqlDbType.Text));
        command.Parameters.Add(new NpgsqlParameter<decimal>("bill_quantity", NpgsqlDbType.Numeric));
        command.Parameters.Add(new NpgsqlParameter<decimal>("covered_quantity", NpgsqlDbType.Numeric));
        command.Parameters.Add(new NpgsqlParameter<decimal>("remaining_quantity", NpgsqlDbType.Numeric));
        command.Parameters.Add(new NpgsqlParameter<string>("summary", NpgsqlDbType.Text));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("first_detected_at", NpgsqlDbType.TimestampTz));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("last_detected_at", NpgsqlDbType.TimestampTz));

        foreach (var discrepancy in discrepancies)
        {
            var key = (discrepancy.BillDocumentId, discrepancy.BillLineNumber, discrepancy.DiscrepancyType);
            command.Parameters["id"].Value = Guid.NewGuid();
            command.Parameters["bill_id"].Value = discrepancy.BillDocumentId;
            command.Parameters["bill_line_number"].Value = discrepancy.BillLineNumber;
            command.Parameters["discrepancy_type"].Value = discrepancy.DiscrepancyType;
            command.Parameters["investigation_status"].Value = discrepancy.InvestigationStatus;
            command.Parameters["item_id"].Value = discrepancy.ItemId;
            command.Parameters["warehouse_id"].Value = discrepancy.WarehouseId;
            command.Parameters["uom_code"].Value = discrepancy.UomCode;
            command.Parameters["bill_quantity"].Value = Round6(discrepancy.BillQuantity);
            command.Parameters["covered_quantity"].Value = Round6(discrepancy.CoveredQuantity);
            command.Parameters["remaining_quantity"].Value = Round6(discrepancy.RemainingQuantity);
            command.Parameters["summary"].Value = discrepancy.Summary;
            command.Parameters["first_detected_at"].Value = existingDiscrepancies.TryGetValue(key, out var firstDetectedAt)
                ? firstDetectedAt
                : detectedAt;
            command.Parameters["last_detected_at"].Value = detectedAt;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void BindGroups(NpgsqlCommand command, IReadOnlyList<BillReceiptMatchingAnchor> groups)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("vendor_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = groups.Select(static group => group.VendorId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("item_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = groups.Select(static group => group.ItemId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("warehouse_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = groups.Select(static group => group.WarehouseId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<string[]>("uom_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = groups.Select(static group => group.UomCode).ToArray()
        });
    }

    private static string ResolveOverallMatchStatus(int billInboundLineCount, decimal billInboundQuantity, decimal coveredQuantity) =>
        billInboundLineCount == 0
            ? "no_inventory_handoff"
            : BillReceiptMatchingPolicy.ResolveCoverageStatus(billInboundQuantity, coveredQuantity);

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private static async Task EnsureSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            create table if not exists {ReceiptsTableName} (
              id uuid primary key,
              company_id char(7) not null,
              entity_number char(11) not null,
              receipt_number text not null,
              vendor_id uuid not null,
              warehouse_id uuid not null,
              status text not null,
              receipt_date date not null,
              vendor_reference text null,
              source_reference text null,
              memo text null,
              created_by_user_id char(7) not null,
              created_at timestamptz not null default now(),
              updated_by_user_id char(7) null,
              updated_at timestamptz not null default now(),
              posted_by_user_id char(7) null,
              posted_at timestamptz null
            );

            create table if not exists {ReceiptLinesTableName} (
              id uuid primary key,
              company_id char(7) not null,
              receipt_id uuid not null,
              line_number integer not null,
              item_id uuid not null,
              quantity numeric(18,6) not null,
              uom_code text not null,
              tracking_capture_home text null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            create table if not exists {AllocationsTableName} (
              id uuid primary key,
              company_id char(7) not null,
              vendor_id uuid not null,
              item_id uuid not null,
              warehouse_id uuid not null,
              uom_code text not null,
              bill_id uuid not null,
              bill_line_number integer not null,
              receipt_id uuid not null,
              receipt_line_number integer not null,
              matched_quantity numeric(18,6) not null,
              created_at timestamptz not null default now()
            );

            create table if not exists {DiscrepanciesTableName} (
              id uuid primary key,
              company_id char(7) not null,
              bill_id uuid not null,
              bill_line_number integer not null,
              discrepancy_type text not null,
              investigation_status text not null,
              item_id uuid not null,
              warehouse_id uuid not null,
              uom_code text not null,
              bill_quantity numeric(18,6) not null,
              covered_quantity numeric(18,6) not null,
              remaining_quantity numeric(18,6) not null,
              summary text not null,
              first_detected_at timestamptz not null,
              last_detected_at timestamptz not null
            );

            create unique index if not exists ux_bill_receipt_matching_allocations_natural
              on {AllocationsTableName} (company_id, bill_id, bill_line_number, receipt_id, receipt_line_number);

            create index if not exists ix_bill_receipt_matching_allocations_bill
              on {AllocationsTableName} (company_id, bill_id, bill_line_number);

            create index if not exists ix_bill_receipt_matching_allocations_receipt
              on {AllocationsTableName} (company_id, receipt_id, receipt_line_number);

            create index if not exists ix_bill_receipt_matching_allocations_anchor
              on {AllocationsTableName} (company_id, vendor_id, item_id, warehouse_id, uom_code);

            create unique index if not exists ux_bill_receipt_matching_discrepancy_lanes_natural
              on {DiscrepanciesTableName} (company_id, bill_id, bill_line_number, discrepancy_type);

            create index if not exists ix_bill_receipt_matching_discrepancy_lanes_bill
              on {DiscrepanciesTableName} (company_id, bill_id, investigation_status);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
