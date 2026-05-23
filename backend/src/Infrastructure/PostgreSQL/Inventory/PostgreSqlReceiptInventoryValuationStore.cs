using Citus.Modules.Inventory.Application.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlReceiptInventoryValuationStore : IReceiptInventoryValuationStore
{
    private const string ActivationLinesTableName = "receipt_inventory_activation_lines";
    private const string MatchingAllocationsTableName = "bill_receipt_matching_allocations";
    private const string ValuationLinesTableName = "receipt_inventory_valuation_lines";

    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly InventoryReceiptExecutionContextAccessor _executionContextAccessor;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlReceiptInventoryValuationStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore,
        InventoryReceiptExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await _foundationStore.EnsureSchemaAsync(cancellationToken);
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: true);
    }

    public async Task<ReceiptInventoryValuationSummary> RefreshReceiptValuationAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        // M4 (P2-10): join the receipt workflow's ambient tx so this
        // step rolls back atomically with activation + emission.
        if (_executionContextAccessor.Current is { } ambient)
        {
            await EnsureSchemaAsync(ambient.Connection, cancellationToken, allowCreate: false);
            return await RefreshReceiptValuationCoreAsync(
                ambient.Connection,
                ambient.Transaction,
                companyId,
                userId,
                receiptDocumentId,
                cancellationToken);
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);

        if (!await TableExistsAsync(connection, ActivationLinesTableName, cancellationToken) ||
            !await TableExistsAsync(connection, MatchingAllocationsTableName, cancellationToken))
        {
            return await LoadReceiptValuationSummaryAsync(connection, null, companyId, receiptDocumentId, hasActivationLines: false, hasMatchingAllocations: false, cancellationToken)
                ?? BuildEmptySummary(receiptDocumentId);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var summary = await RefreshReceiptValuationCoreAsync(
                connection,
                transaction,
                companyId,
                userId,
                receiptDocumentId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return summary;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original failure.
            }
            throw;
        }
    }

    private async Task<ReceiptInventoryValuationSummary> RefreshReceiptValuationCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        // Table-existence check is a no-op for the ambient-tx path
        // because the workflow's activation step already ran on the
        // same tx and would have failed earlier if these tables
        // didn't exist. Repeating the check here is harmless and
        // keeps the helper self-contained for the standalone path.
        if (!await TableExistsAsync(connection, ActivationLinesTableName, cancellationToken) ||
            !await TableExistsAsync(connection, MatchingAllocationsTableName, cancellationToken))
        {
            return await LoadReceiptValuationSummaryAsync(connection, transaction, companyId, receiptDocumentId, hasActivationLines: false, hasMatchingAllocations: false, cancellationToken)
                ?? BuildEmptySummary(receiptDocumentId);
        }

        await InsertValuationLinesAsync(
            connection,
            transaction,
            companyId,
            userId,
            receiptDocumentId,
            cancellationToken);

        var summary = await LoadReceiptValuationSummaryAsync(
            connection,
            transaction,
            companyId,
            receiptDocumentId,
            hasActivationLines: true,
            hasMatchingAllocations: true,
            cancellationToken);
        return summary ?? BuildEmptySummary(receiptDocumentId);
    }

    public async Task<ReceiptInventoryValuationSummary?> GetReceiptValuationSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        var hasActivationLines = await TableExistsAsync(connection, ActivationLinesTableName, cancellationToken);
        var hasMatchingAllocations = await TableExistsAsync(connection, MatchingAllocationsTableName, cancellationToken);

        return await LoadReceiptValuationSummaryAsync(
            connection,
            null,
            companyId,
            receiptDocumentId,
            hasActivationLines,
            hasMatchingAllocations,
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, ReceiptInventoryValuationSummary>> GetReceiptValuationSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentIds.Count == 0)
        {
            return new Dictionary<Guid, ReceiptInventoryValuationSummary>();
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        var hasActivationLines = await TableExistsAsync(connection, ActivationLinesTableName, cancellationToken);
        var hasMatchingAllocations = await TableExistsAsync(connection, MatchingAllocationsTableName, cancellationToken);

        return await LoadReceiptValuationSummariesAsync(
            connection,
            null,
            companyId,
            receiptDocumentIds.Distinct().ToArray(),
            hasActivationLines,
            hasMatchingAllocations,
            cancellationToken);
    }

    private static async Task InsertValuationLinesAsync(
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
            with ordered_allocations as (
              select
                a.company_id,
                a.receipt_id,
                a.receipt_line_number,
                a.bill_id,
                a.bill_line_number,
                a.item_id,
                a.warehouse_id,
                upper(trim(a.uom_code)) as uom_code,
                a.matched_quantity,
                al.activated_quantity,
                coalesce(
                  sum(a.matched_quantity) over (
                    partition by a.company_id, a.receipt_id, a.receipt_line_number
                    order by a.bill_id, a.bill_line_number
                    rows between unbounded preceding and 1 preceding
                  ),
                  0
                ) as prior_matched_quantity,
                b.document_currency_code,
                b.base_currency_code,
                b.fx_rate,
                bl.unit_cost
              from {MatchingAllocationsTableName} a
              join {ActivationLinesTableName} al
                on al.company_id = a.company_id
               and al.receipt_id = a.receipt_id
               and al.receipt_line_number = a.receipt_line_number
              join bills b
                on b.company_id = a.company_id
               and b.id = a.bill_id
              join bill_lines bl
                on bl.company_id = a.company_id
               and bl.bill_id = a.bill_id
               and bl.line_number = a.bill_line_number
              where a.company_id = @company_id
                and a.receipt_id = @receipt_id
                and b.status in ('submitted', 'posted')
                and bl.unit_cost is not null
            ),
            bounded_allocations as (
              select
                *,
                greatest(
                  0,
                  least(matched_quantity, activated_quantity - prior_matched_quantity)
                )::numeric(20,6) as valued_quantity
              from ordered_allocations
            )
            insert into {ValuationLinesTableName} (
              id,
              company_id,
              receipt_id,
              receipt_line_number,
              bill_id,
              bill_line_number,
              item_id,
              warehouse_id,
              uom_code,
              valued_quantity,
              document_currency_code,
              base_currency_code,
              fx_rate_to_base,
              unit_cost_tx,
              unit_cost_base,
              extended_cost_base,
              valuation_source,
              valued_by_user_id,
              valued_at
            )
            select
              gen_random_uuid(),
              company_id,
              receipt_id,
              receipt_line_number,
              bill_id,
              bill_line_number,
              item_id,
              warehouse_id,
              uom_code,
              valued_quantity,
              upper(trim(document_currency_code)),
              upper(trim(base_currency_code)),
              fx_rate,
              unit_cost,
              round(unit_cost * fx_rate, 6),
              round(valued_quantity * round(unit_cost * fx_rate, 6), 6),
              'bill_receipt_matching',
              @user_id,
              now()
            from bounded_allocations
            where valued_quantity > 0
            on conflict (company_id, receipt_id, receipt_line_number, bill_id, bill_line_number)
            do nothing;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ReceiptInventoryValuationSummary?> LoadReceiptValuationSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        bool hasActivationLines,
        bool hasMatchingAllocations,
        CancellationToken cancellationToken)
    {
        var summaries = await LoadReceiptValuationSummariesAsync(
            connection,
            transaction,
            companyId,
            [receiptDocumentId],
            hasActivationLines,
            hasMatchingAllocations,
            cancellationToken);
        return summaries.TryGetValue(receiptDocumentId, out var summary) ? summary : null;
    }

    private static async Task<IReadOnlyDictionary<Guid, ReceiptInventoryValuationSummary>> LoadReceiptValuationSummariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid[] receiptDocumentIds,
        bool hasActivationLines,
        bool hasMatchingAllocations,
        CancellationToken cancellationToken)
    {
        var activationGroupsSql = hasActivationLines
            ? $"""
               select
                 a.receipt_id as receipt_document_id,
                 coalesce(sum(a.activated_quantity), 0)::numeric(20,6) as activated_quantity
               from {ActivationLinesTableName} a
               where a.company_id = @company_id
                 and a.receipt_id = any(@receipt_document_ids)
               group by a.receipt_id
               """
            : """
              select
                null::uuid as receipt_document_id,
                0::numeric(20,6) as activated_quantity
              where false
              """;
        var coverageGroupsSql = hasMatchingAllocations
            ? $"""
               select
                 a.receipt_id as receipt_document_id,
                 coalesce(sum(a.matched_quantity), 0)::numeric(20,6) as bill_covered_quantity
               from {MatchingAllocationsTableName} a
               join bills b
                 on b.company_id = a.company_id
                and b.id = a.bill_id
               join bill_lines bl
                 on bl.company_id = a.company_id
                and bl.bill_id = a.bill_id
                and bl.line_number = a.bill_line_number
               where a.company_id = @company_id
                 and a.receipt_id = any(@receipt_document_ids)
                 and b.status in ('submitted', 'posted')
                 and bl.unit_cost is not null
               group by a.receipt_id
               """
            : """
              select
                null::uuid as receipt_document_id,
                0::numeric(20,6) as bill_covered_quantity
              where false
              """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with requested_receipts as (
              select unnest(@receipt_document_ids::uuid[]) as receipt_document_id
            ),
            activation_groups as (
              {activationGroupsSql}
            ),
            coverage_groups as (
              {coverageGroupsSql}
            ),
            valuation_groups as (
              select
                v.receipt_id as receipt_document_id,
                count(*)::int as valuation_line_count,
                coalesce(sum(v.valued_quantity), 0)::numeric(20,6) as valued_quantity,
                coalesce(sum(v.extended_cost_base), 0)::numeric(20,6) as valuation_amount_base,
                max(v.valued_at) as last_valued_at
              from {ValuationLinesTableName} v
              where v.company_id = @company_id
                and v.receipt_id = any(@receipt_document_ids)
              group by v.receipt_id
            )
            select
              rr.receipt_document_id,
              coalesce(ag.activated_quantity, 0)::numeric(20,6) as activated_quantity,
              coalesce(cg.bill_covered_quantity, 0)::numeric(20,6) as bill_covered_quantity,
              coalesce(vg.valuation_line_count, 0) as valuation_line_count,
              coalesce(vg.valued_quantity, 0)::numeric(20,6) as valued_quantity,
              coalesce(vg.valuation_amount_base, 0)::numeric(20,6) as valuation_amount_base,
              vg.last_valued_at
            from requested_receipts rr
            left join activation_groups ag
              on ag.receipt_document_id = rr.receipt_document_id
            left join coverage_groups cg
              on cg.receipt_document_id = rr.receipt_document_id
            left join valuation_groups vg
              on vg.receipt_document_id = rr.receipt_document_id;
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("receipt_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = receiptDocumentIds
        });
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var summaries = new Dictionary<Guid, ReceiptInventoryValuationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var receiptDocumentId = reader.GetGuid(reader.GetOrdinal("receipt_document_id"));
            var activatedQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("activated_quantity")));
            var billCoveredQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("bill_covered_quantity")));
            var valuedQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("valued_quantity")));
            var valuationStatus = ReceiptInventoryValuationStatusPolicy.Resolve(
                activatedQuantity,
                billCoveredQuantity,
                valuedQuantity);

            summaries[receiptDocumentId] = new ReceiptInventoryValuationSummary(
                receiptDocumentId,
                valuationStatus,
                activatedQuantity,
                billCoveredQuantity,
                valuedQuantity,
                Round6(Math.Max(0m, activatedQuantity - valuedQuantity)),
                reader.GetInt32(reader.GetOrdinal("valuation_line_count")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("valuation_amount_base"))),
                reader.IsDBNull(reader.GetOrdinal("last_valued_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_valued_at")));
        }

        return summaries;
    }

    private async Task EnsureSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        bool allowCreate)
    {
        if (_schemaEnsured)
        {
            return;
        }

        if (await TableExistsAsync(connection, ValuationLinesTableName, cancellationToken))
        {
            _schemaEnsured = true;
            return;
        }

        if (!allowCreate)
        {
            throw new InvalidOperationException(
                "Receipt inventory valuation schema has not been installed. Apply database migrations before valuing receipts.");
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            if (await TableExistsAsync(connection, ValuationLinesTableName, cancellationToken))
            {
                _schemaEnsured = true;
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                create table if not exists {ValuationLinesTableName} (
                  id uuid primary key default gen_random_uuid(),
                  company_id char(7) not null references companies(id) on delete cascade,
                  receipt_id uuid not null,
                  receipt_line_number integer not null,
                  bill_id uuid not null,
                  bill_line_number integer not null,
                  item_id uuid not null references inventory_items(id) on delete cascade,
                  warehouse_id uuid not null references inventory_warehouses(id) on delete cascade,
                  uom_code text not null,
                  valued_quantity numeric(20, 6) not null,
                  document_currency_code text not null,
                  base_currency_code text not null,
                  fx_rate_to_base numeric(20, 8) not null,
                  unit_cost_tx numeric(20, 6) not null,
                  unit_cost_base numeric(20, 6) not null,
                  extended_cost_base numeric(20, 6) not null,
                  valuation_source text not null,
                  valued_by_user_id char(7) not null,
                  valued_at timestamptz not null default now()
                );

                create unique index if not exists ux_receipt_inventory_valuation_lines_natural
                  on {ValuationLinesTableName} (company_id, receipt_id, receipt_line_number, bill_id, bill_line_number);

                create index if not exists ix_receipt_inventory_valuation_lines_receipt
                  on {ValuationLinesTableName} (company_id, receipt_id, valued_at desc);

                create index if not exists ix_receipt_inventory_valuation_lines_bill
                  on {ValuationLinesTableName} (company_id, bill_id, bill_line_number);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaEnsured = true;
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

    private static ReceiptInventoryValuationSummary BuildEmptySummary(Guid receiptDocumentId) =>
        new(
            receiptDocumentId,
            ReceiptInventoryValuationStatusPolicy.NoQuantityActivation,
            0m,
            0m,
            0m,
            0m,
            0,
            0m,
            null);

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
