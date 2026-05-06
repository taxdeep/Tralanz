using Citus.Modules.Inventory.Application.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlReceiptGrIrBridgeStore : IReceiptGrIrBridgeStore
{
    private const string ActivationLinesTableName = "receipt_inventory_activation_lines";
    private const string EmissionLinesTableName = "receipt_inventory_cost_layer_emission_lines";
    private const string BridgeLinesTableName = "receipt_grir_bridge_lines";

    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlReceiptGrIrBridgeStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
    }

    public async Task<ReceiptGrIrBridgeSummary> RefreshReceiptGrIrBridgeAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        if (!await EnsureSchemaAsync(connection, cancellationToken))
        {
            return BuildEmptySummary(receiptDocumentId);
        }

        var hasActivationLines = await TableExistsAsync(connection, ActivationLinesTableName, cancellationToken);
        if (!hasActivationLines)
        {
            return BuildEmptySummary(receiptDocumentId);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await AcquireReceiptGrIrBridgeLockAsync(connection, transaction, companyId, receiptDocumentId, cancellationToken);
            await UpsertBridgeLinesAsync(
                connection,
                transaction,
                companyId,
                userId,
                receiptDocumentId,
                cancellationToken);

            var summary = await LoadReceiptGrIrBridgeSummaryAsync(
                connection,
                transaction,
                companyId,
                receiptDocumentId,
                hasBridgeLines: true,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return summary ?? BuildEmptySummary(receiptDocumentId);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ReceiptGrIrBridgeSummary?> GetReceiptGrIrBridgeSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        var hasBridgeLines = await EnsureSchemaAsync(connection, cancellationToken);
        return await LoadReceiptGrIrBridgeSummaryAsync(
            connection,
            null,
            companyId,
            receiptDocumentId,
            hasBridgeLines,
            cancellationToken) ?? BuildEmptySummary(receiptDocumentId);
    }

    public async Task<IReadOnlyDictionary<Guid, ReceiptGrIrBridgeSummary>> GetReceiptGrIrBridgeSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentIds.Count == 0)
        {
            return new Dictionary<Guid, ReceiptGrIrBridgeSummary>();
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        var distinctReceiptIds = receiptDocumentIds.Distinct().ToArray();
        var hasBridgeLines = await EnsureSchemaAsync(connection, cancellationToken);
        return await LoadReceiptGrIrBridgeSummariesAsync(
            connection,
            null,
            companyId,
            distinctReceiptIds,
            hasBridgeLines,
            cancellationToken);
    }

    private static async Task AcquireReceiptGrIrBridgeLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select pg_advisory_xact_lock(hashtext(@lock_key));";
        command.Parameters.AddWithValue("lock_key", $"receipt-grir-bridge:{companyId.Value}:{receiptDocumentId:N}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertBridgeLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with activation_lines as (
              select
                a.receipt_id,
                a.inventory_document_id,
                a.inventory_document_line_id
              from {ActivationLinesTableName} a
              where a.company_id = @company_id
                and a.receipt_id = @receipt_id
            ),
            orphan_cost_layer_count as (
              select
                count(cl.id) filter (where existing_emission.id is null)::int as orphan_count
              from activation_lines al
              join inventory_cost_layers cl
                on cl.company_id = @company_id
               and cl.source_document_id = al.inventory_document_id
               and cl.source_document_line_id = al.inventory_document_line_id
              left join {EmissionLinesTableName} existing_emission
                on existing_emission.company_id = @company_id
               and existing_emission.cost_layer_id = cl.id
            ),
            emission_truth as (
              select
                e.id as cost_layer_emission_line_id,
                e.company_id,
                e.receipt_id,
                e.receipt_line_number,
                e.valuation_line_id,
                e.cost_layer_id,
                e.bill_id,
                e.bill_line_number,
                e.item_id,
                e.warehouse_id,
                e.uom_code,
                e.emitted_quantity,
                e.emitted_cost_base,
                b.status as bill_status,
                cl.id as matched_cost_layer_id,
                cl.original_qty as cost_layer_quantity,
                round(cl.original_qty * cl.unit_cost_base, 6) as cost_layer_original_cost_base,
                coalesce((select orphan_count from orphan_cost_layer_count), 0) as orphan_count
              from {EmissionLinesTableName} e
              join bills b
                on b.company_id = e.company_id
               and b.id = e.bill_id
              left join inventory_cost_layers cl
                on cl.company_id = e.company_id
               and cl.id = e.cost_layer_id
              where e.company_id = @company_id
                and e.receipt_id = @receipt_id
            ),
            classified_truth as (
              select
                *,
                case
                  when bill_status <> 'posted' then '{ReceiptGrIrBridgeStatusPolicy.NotEligible}'
                  when matched_cost_layer_id is null then '{ReceiptGrIrBridgeStatusPolicy.BlockedReconciliationRequired}'
                  when orphan_count > 0 then '{ReceiptGrIrBridgeStatusPolicy.BlockedReconciliationRequired}'
                  when round(cost_layer_quantity, 6) <> round(emitted_quantity, 6) then '{ReceiptGrIrBridgeStatusPolicy.BlockedReconciliationRequired}'
                  when round(cost_layer_original_cost_base, 6) <> round(emitted_cost_base, 6) then '{ReceiptGrIrBridgeStatusPolicy.BlockedReconciliationRequired}'
                  else '{ReceiptGrIrBridgeStatusPolicy.EligibleNotPosted}'
                end as bridge_status,
                case
                  when bill_status <> 'posted' then 'bill_not_posted'
                  when matched_cost_layer_id is null then 'cost_layer_missing'
                  when orphan_count > 0 then 'orphan_cost_layer'
                  when round(cost_layer_quantity, 6) <> round(emitted_quantity, 6) then 'quantity_mismatch'
                  when round(cost_layer_original_cost_base, 6) <> round(emitted_cost_base, 6) then 'amount_mismatch'
                  else null
                end as blocked_reason_code
              from emission_truth
            )
            insert into {BridgeLinesTableName} (
              id,
              company_id,
              receipt_id,
              receipt_line_number,
              valuation_line_id,
              cost_layer_emission_line_id,
              cost_layer_id,
              bill_id,
              bill_line_number,
              item_id,
              warehouse_id,
              uom_code,
              bridge_quantity,
              bridge_amount_base,
              bridge_status,
              blocked_reason_code,
              refreshed_by_user_id,
              refreshed_at
            )
            select
              gen_random_uuid(),
              company_id,
              receipt_id,
              receipt_line_number,
              valuation_line_id,
              cost_layer_emission_line_id,
              cost_layer_id,
              bill_id,
              bill_line_number,
              item_id,
              warehouse_id,
              uom_code,
              emitted_quantity,
              emitted_cost_base,
              bridge_status,
              blocked_reason_code,
              @user_id,
              now()
            from classified_truth
            where emitted_quantity > 0
            on conflict (company_id, cost_layer_emission_line_id)
            do update set
              receipt_id = excluded.receipt_id,
              receipt_line_number = excluded.receipt_line_number,
              valuation_line_id = excluded.valuation_line_id,
              cost_layer_id = excluded.cost_layer_id,
              bill_id = excluded.bill_id,
              bill_line_number = excluded.bill_line_number,
              item_id = excluded.item_id,
              warehouse_id = excluded.warehouse_id,
              uom_code = excluded.uom_code,
              bridge_quantity = excluded.bridge_quantity,
              bridge_amount_base = excluded.bridge_amount_base,
              bridge_status = case
                when {BridgeLinesTableName}.bridge_status in ('{ReceiptGrIrBridgeStatusPolicy.Posted}', '{ReceiptGrIrBridgeStatusPolicy.PartiallyPosted}')
                  then {BridgeLinesTableName}.bridge_status
                else excluded.bridge_status
              end,
              blocked_reason_code = case
                when {BridgeLinesTableName}.bridge_status in ('{ReceiptGrIrBridgeStatusPolicy.Posted}', '{ReceiptGrIrBridgeStatusPolicy.PartiallyPosted}')
                  then {BridgeLinesTableName}.blocked_reason_code
                else excluded.blocked_reason_code
              end,
              refreshed_by_user_id = excluded.refreshed_by_user_id,
              refreshed_at = excluded.refreshed_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ReceiptGrIrBridgeSummary?> LoadReceiptGrIrBridgeSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        bool hasBridgeLines,
        CancellationToken cancellationToken)
    {
        var summaries = await LoadReceiptGrIrBridgeSummariesAsync(
            connection,
            transaction,
            companyId,
            [receiptDocumentId],
            hasBridgeLines,
            cancellationToken);
        return summaries.TryGetValue(receiptDocumentId, out var summary) ? summary : null;
    }

    private static async Task<IReadOnlyDictionary<Guid, ReceiptGrIrBridgeSummary>> LoadReceiptGrIrBridgeSummariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid[] receiptDocumentIds,
        bool hasBridgeLines,
        CancellationToken cancellationToken)
    {
        var bridgeGroupsSql = hasBridgeLines
            ? $"""
               select
                 b.receipt_id as receipt_document_id,
                 count(*)::int as bridge_line_count,
                 count(*) filter (where b.bridge_status = '{ReceiptGrIrBridgeStatusPolicy.EligibleNotPosted}')::int as eligible_line_count,
                 count(*) filter (where b.bridge_status = '{ReceiptGrIrBridgeStatusPolicy.BlockedReconciliationRequired}')::int as blocked_reconciliation_line_count,
                 count(*) filter (where b.bridge_status = '{ReceiptGrIrBridgeStatusPolicy.BlockedVarianceRequired}')::int as blocked_variance_line_count,
                 count(*) filter (where b.bridge_status = '{ReceiptGrIrBridgeStatusPolicy.Posted}')::int as posted_line_count,
                 count(*) filter (where b.bridge_status = '{ReceiptGrIrBridgeStatusPolicy.PartiallyPosted}')::int as partially_posted_line_count,
                 coalesce(sum(b.bridge_quantity), 0)::numeric(20,6) as bridge_quantity,
                 coalesce(sum(b.bridge_amount_base), 0)::numeric(20,6) as bridge_amount_base,
                 coalesce(sum(b.bridge_amount_base) filter (where b.bridge_status = '{ReceiptGrIrBridgeStatusPolicy.EligibleNotPosted}'), 0)::numeric(20,6) as eligible_amount_base,
                 coalesce(sum(b.bridge_amount_base) filter (where b.bridge_status in ('{ReceiptGrIrBridgeStatusPolicy.BlockedReconciliationRequired}', '{ReceiptGrIrBridgeStatusPolicy.BlockedVarianceRequired}')), 0)::numeric(20,6) as blocked_amount_base,
                 coalesce(sum(b.bridge_amount_base) filter (where b.bridge_status = '{ReceiptGrIrBridgeStatusPolicy.Posted}'), 0)::numeric(20,6) as posted_amount_base,
                 (array_agg(b.journal_entry_id order by b.posted_at desc nulls last) filter (where b.journal_entry_id is not null))[1] as journal_entry_id,
                 (array_agg(b.journal_entry_display_number order by b.posted_at desc nulls last) filter (where b.journal_entry_display_number is not null))[1] as journal_entry_display_number,
                 max(b.posted_at) as last_posted_at,
                 max(b.refreshed_at) as last_refreshed_at
               from {BridgeLinesTableName} b
               where b.company_id = @company_id
                 and b.receipt_id = any(@receipt_document_ids)
               group by b.receipt_id
               """
            : """
              select
                null::uuid as receipt_document_id,
                0 as bridge_line_count,
                0 as eligible_line_count,
                0 as blocked_reconciliation_line_count,
                0 as blocked_variance_line_count,
                0 as posted_line_count,
                0 as partially_posted_line_count,
                0::numeric(20,6) as bridge_quantity,
                0::numeric(20,6) as bridge_amount_base,
                0::numeric(20,6) as eligible_amount_base,
                0::numeric(20,6) as blocked_amount_base,
                0::numeric(20,6) as posted_amount_base,
                null::uuid as journal_entry_id,
                null::text as journal_entry_display_number,
                null::timestamptz as last_posted_at,
                null::timestamptz as last_refreshed_at
              where false
              """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with requested_receipts as (
              select unnest(@receipt_document_ids::uuid[]) as receipt_document_id
            ),
            bridge_groups as (
              {bridgeGroupsSql}
            )
            select
              rr.receipt_document_id,
              coalesce(bg.bridge_line_count, 0) as bridge_line_count,
              coalesce(bg.eligible_line_count, 0) as eligible_line_count,
              coalesce(bg.blocked_reconciliation_line_count, 0) as blocked_reconciliation_line_count,
              coalesce(bg.blocked_variance_line_count, 0) as blocked_variance_line_count,
              coalesce(bg.posted_line_count, 0) as posted_line_count,
              coalesce(bg.partially_posted_line_count, 0) as partially_posted_line_count,
              coalesce(bg.bridge_quantity, 0)::numeric(20,6) as bridge_quantity,
              coalesce(bg.bridge_amount_base, 0)::numeric(20,6) as bridge_amount_base,
              coalesce(bg.eligible_amount_base, 0)::numeric(20,6) as eligible_amount_base,
              coalesce(bg.blocked_amount_base, 0)::numeric(20,6) as blocked_amount_base,
              coalesce(bg.posted_amount_base, 0)::numeric(20,6) as posted_amount_base,
              bg.journal_entry_id,
              bg.journal_entry_display_number,
              bg.last_posted_at,
              bg.last_refreshed_at
            from requested_receipts rr
            left join bridge_groups bg
              on bg.receipt_document_id = rr.receipt_document_id;
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("receipt_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = receiptDocumentIds
        });
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var summaries = new Dictionary<Guid, ReceiptGrIrBridgeSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var receiptDocumentId = reader.GetGuid(reader.GetOrdinal("receipt_document_id"));
            var bridgeLineCount = reader.GetInt32(reader.GetOrdinal("bridge_line_count"));
            var eligibleLineCount = reader.GetInt32(reader.GetOrdinal("eligible_line_count"));
            var blockedReconciliationLineCount = reader.GetInt32(reader.GetOrdinal("blocked_reconciliation_line_count"));
            var blockedVarianceLineCount = reader.GetInt32(reader.GetOrdinal("blocked_variance_line_count"));
            var postedLineCount = reader.GetInt32(reader.GetOrdinal("posted_line_count"));
            var partiallyPostedLineCount = reader.GetInt32(reader.GetOrdinal("partially_posted_line_count"));
            var bridgeStatus = ReceiptGrIrBridgeStatusPolicy.Resolve(
                bridgeLineCount,
                eligibleLineCount,
                blockedReconciliationLineCount,
                blockedVarianceLineCount,
                postedLineCount,
                partiallyPostedLineCount);

            summaries[receiptDocumentId] = new ReceiptGrIrBridgeSummary(
                receiptDocumentId,
                bridgeStatus,
                bridgeLineCount,
                eligibleLineCount,
                blockedReconciliationLineCount,
                blockedVarianceLineCount,
                postedLineCount,
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("bridge_quantity"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("bridge_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("eligible_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("blocked_amount_base"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("posted_amount_base"))),
                reader.IsDBNull(reader.GetOrdinal("journal_entry_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("journal_entry_id")),
                reader.IsDBNull(reader.GetOrdinal("journal_entry_display_number"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("journal_entry_display_number")),
                reader.IsDBNull(reader.GetOrdinal("last_posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_posted_at")),
                reader.IsDBNull(reader.GetOrdinal("last_refreshed_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_refreshed_at")));
        }

        return summaries;
    }

    private async Task<bool> EnsureSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return true;
        }

        if (!await TableExistsAsync(connection, EmissionLinesTableName, cancellationToken))
        {
            return false;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return true;
            }

            if (!await TableExistsAsync(connection, EmissionLinesTableName, cancellationToken))
            {
                return false;
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                create table if not exists {BridgeLinesTableName} (
                  id uuid primary key default gen_random_uuid(),
                  company_id char(7) not null references companies(id) on delete cascade,
                  receipt_id uuid not null,
                  receipt_line_number integer not null,
                  valuation_line_id uuid not null,
                  cost_layer_emission_line_id uuid not null references {EmissionLinesTableName}(id) on delete cascade,
                  cost_layer_id uuid not null,
                  bill_id uuid not null,
                  bill_line_number integer not null,
                  item_id uuid not null references inventory_items(id) on delete cascade,
                  warehouse_id uuid not null references inventory_warehouses(id) on delete cascade,
                  uom_code text not null,
                  bridge_quantity numeric(20, 6) not null,
                  bridge_amount_base numeric(20, 6) not null,
                  bridge_status text not null,
                  blocked_reason_code text null,
                  journal_entry_id uuid null,
                  journal_entry_display_number text null,
                  posted_by_user_id char(7) null,
                  posted_at timestamptz null,
                  refreshed_by_user_id char(7) not null,
                  refreshed_at timestamptz not null default now()
                );

                alter table {BridgeLinesTableName}
                  add column if not exists journal_entry_id uuid null;

                alter table {BridgeLinesTableName}
                  add column if not exists journal_entry_display_number text null;

                alter table {BridgeLinesTableName}
                  add column if not exists posted_by_user_id char(7) null;

                alter table {BridgeLinesTableName}
                  add column if not exists posted_at timestamptz null;

                create unique index if not exists ux_receipt_grir_bridge_lines_emission
                  on {BridgeLinesTableName} (company_id, cost_layer_emission_line_id);

                create index if not exists ix_receipt_grir_bridge_lines_receipt
                  on {BridgeLinesTableName} (company_id, receipt_id, refreshed_at desc);

                create index if not exists ix_receipt_grir_bridge_lines_bill
                  on {BridgeLinesTableName} (company_id, bill_id, bill_line_number);

                create index if not exists ix_receipt_grir_bridge_lines_status
                  on {BridgeLinesTableName} (company_id, bridge_status);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaEnsured = true;
            return true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select to_regclass(@table_name) is not null;";
        command.Parameters.AddWithValue("table_name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    private static ReceiptGrIrBridgeSummary BuildEmptySummary(Guid receiptDocumentId) =>
        new(
            receiptDocumentId,
            ReceiptGrIrBridgeStatusPolicy.NotEligible,
            0,
            0,
            0,
            0,
            0,
            0m,
            0m,
            0m,
            0m,
            0m,
            null,
            null,
            null,
            null);

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
