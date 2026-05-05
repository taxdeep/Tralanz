using Citus.Modules.Inventory.Application.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlReceiptInventoryCostLayerEmissionStore : IReceiptInventoryCostLayerEmissionStore
{
    private const string ActivationLinesTableName = "receipt_inventory_activation_lines";
    private const string ValuationLinesTableName = "receipt_inventory_valuation_lines";
    private const string EmissionLinesTableName = "receipt_inventory_cost_layer_emission_lines";

    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlReceiptInventoryCostLayerEmissionStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
    }

    public async Task<ReceiptInventoryCostLayerEmissionSummary> EmitReceiptCostLayersAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var hasActivationLines = await TableExistsAsync(connection, ActivationLinesTableName, cancellationToken);
        var hasValuationLines = await TableExistsAsync(connection, ValuationLinesTableName, cancellationToken);
        if (!hasActivationLines || !hasValuationLines)
        {
            return await LoadReceiptCostLayerEmissionSummaryAsync(
                connection,
                null,
                companyId,
                receiptDocumentId,
                hasActivationLines,
                hasValuationLines,
                hasEmissionLines: true,
                cancellationToken)
                ?? BuildEmptySummary(receiptDocumentId);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await AcquireReceiptEmissionLockAsync(connection, transaction, companyId, receiptDocumentId, cancellationToken);
            await InsertEmissionLinesAsync(
                connection,
                transaction,
                companyId,
                userId,
                receiptDocumentId,
                cancellationToken);

            var summary = await LoadReceiptCostLayerEmissionSummaryAsync(
                connection,
                transaction,
                companyId,
                receiptDocumentId,
                hasActivationLines: true,
                hasValuationLines: true,
                hasEmissionLines: true,
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

    public async Task<ReceiptInventoryCostLayerEmissionSummary?> GetReceiptCostLayerEmissionSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        var hasActivationLines = await TableExistsAsync(connection, ActivationLinesTableName, cancellationToken);
        var hasValuationLines = await TableExistsAsync(connection, ValuationLinesTableName, cancellationToken);

        return await LoadReceiptCostLayerEmissionSummaryAsync(
            connection,
            null,
            companyId,
            receiptDocumentId,
            hasActivationLines,
            hasValuationLines,
            hasEmissionLines: true,
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionSummary>> GetReceiptCostLayerEmissionSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentIds.Count == 0)
        {
            return new Dictionary<Guid, ReceiptInventoryCostLayerEmissionSummary>();
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        var hasActivationLines = await TableExistsAsync(connection, ActivationLinesTableName, cancellationToken);
        var hasValuationLines = await TableExistsAsync(connection, ValuationLinesTableName, cancellationToken);

        return await LoadReceiptCostLayerEmissionSummariesAsync(
            connection,
            null,
            companyId,
            receiptDocumentIds.Distinct().ToArray(),
            hasActivationLines,
            hasValuationLines,
            hasEmissionLines: true,
            cancellationToken);
    }

    public async Task<ReceiptInventoryCostLayerEmissionReconciliationSummary?> GetReceiptCostLayerEmissionReconciliationSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        var hasActivationLines = await TableExistsAsync(connection, ActivationLinesTableName, cancellationToken);

        return await LoadReceiptCostLayerEmissionReconciliationSummaryAsync(
            connection,
            null,
            companyId,
            receiptDocumentId,
            hasActivationLines,
            hasEmissionLines: true,
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionReconciliationSummary>> GetReceiptCostLayerEmissionReconciliationSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentIds.Count == 0)
        {
            return new Dictionary<Guid, ReceiptInventoryCostLayerEmissionReconciliationSummary>();
        }

        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        var hasActivationLines = await TableExistsAsync(connection, ActivationLinesTableName, cancellationToken);

        return await LoadReceiptCostLayerEmissionReconciliationSummariesAsync(
            connection,
            null,
            companyId,
            receiptDocumentIds.Distinct().ToArray(),
            hasActivationLines,
            hasEmissionLines: true,
            cancellationToken);
    }

    private static async Task AcquireReceiptEmissionLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select pg_advisory_xact_lock(hashtext(@lock_key));";
        command.Parameters.AddWithValue("lock_key", $"receipt-cost-layer-emission:{companyId:N}:{receiptDocumentId:N}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEmissionLinesAsync(
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
            with eligible_valuation as (
              select
                gen_random_uuid() as cost_layer_id,
                v.id as valuation_line_id,
                v.company_id,
                v.receipt_id,
                v.receipt_line_number,
                v.bill_id,
                v.bill_line_number,
                v.item_id,
                v.warehouse_id,
                v.uom_code,
                v.valued_quantity,
                v.unit_cost_base,
                v.extended_cost_base,
                al.inventory_document_id,
                al.inventory_document_line_id,
                le.id as source_ledger_entry_id,
                coalesce(le.posting_date, r.receipt_date) as posting_date
              from {ValuationLinesTableName} v
              join {ActivationLinesTableName} al
                on al.company_id = v.company_id
               and al.receipt_id = v.receipt_id
               and al.receipt_line_number = v.receipt_line_number
              join bills b
                on b.company_id = v.company_id
               and b.id = v.bill_id
              join receipts r
                on r.company_id = v.company_id
               and r.id = v.receipt_id
              left join inventory_ledger_entries le
                on le.company_id = al.company_id
               and le.document_id = al.inventory_document_id
               and le.document_line_id = al.inventory_document_line_id
              left join {EmissionLinesTableName} existing
                on existing.company_id = v.company_id
               and existing.valuation_line_id = v.id
              where v.company_id = @company_id
                and v.receipt_id = @receipt_id
                and b.status = 'posted'
                and v.valued_quantity > 0
                and existing.id is null
            ),
            inserted_layers as (
              insert into inventory_cost_layers (
                id,
                company_id,
                item_id,
                warehouse_id,
                source_ledger_entry_id,
                source_document_id,
                source_document_line_id,
                layer_date,
                original_qty,
                remaining_qty,
                unit_cost_base,
                remaining_cost_base
              )
              select
                cost_layer_id,
                company_id,
                item_id,
                warehouse_id,
                source_ledger_entry_id,
                inventory_document_id,
                inventory_document_line_id,
                posting_date,
                valued_quantity,
                valued_quantity,
                unit_cost_base,
                extended_cost_base
              from eligible_valuation
              returning id
            )
            insert into {EmissionLinesTableName} (
              id,
              company_id,
              receipt_id,
              receipt_line_number,
              valuation_line_id,
              cost_layer_id,
              inventory_document_id,
              inventory_document_line_id,
              source_ledger_entry_id,
              bill_id,
              bill_line_number,
              item_id,
              warehouse_id,
              uom_code,
              emitted_quantity,
              emitted_cost_base,
              emitted_by_user_id,
              emitted_at
            )
            select
              gen_random_uuid(),
              ev.company_id,
              ev.receipt_id,
              ev.receipt_line_number,
              ev.valuation_line_id,
              ev.cost_layer_id,
              ev.inventory_document_id,
              ev.inventory_document_line_id,
              ev.source_ledger_entry_id,
              ev.bill_id,
              ev.bill_line_number,
              ev.item_id,
              ev.warehouse_id,
              ev.uom_code,
              ev.valued_quantity,
              ev.extended_cost_base,
              @user_id,
              now()
            from eligible_valuation ev
            join inserted_layers il
              on il.id = ev.cost_layer_id
            on conflict (company_id, valuation_line_id)
            do nothing;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ReceiptInventoryCostLayerEmissionSummary?> LoadReceiptCostLayerEmissionSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        bool hasActivationLines,
        bool hasValuationLines,
        bool hasEmissionLines,
        CancellationToken cancellationToken)
    {
        var summaries = await LoadReceiptCostLayerEmissionSummariesAsync(
            connection,
            transaction,
            companyId,
            [receiptDocumentId],
            hasActivationLines,
            hasValuationLines,
            hasEmissionLines,
            cancellationToken);
        return summaries.TryGetValue(receiptDocumentId, out var summary) ? summary : null;
    }

    private static async Task<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionSummary>> LoadReceiptCostLayerEmissionSummariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid[] receiptDocumentIds,
        bool hasActivationLines,
        bool hasValuationLines,
        bool hasEmissionLines,
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
        var valuationGroupsSql = hasValuationLines
            ? $"""
               select
                 v.receipt_id as receipt_document_id,
                 coalesce(sum(v.valued_quantity), 0)::numeric(20,6) as valuation_backed_quantity,
                 coalesce(sum(v.valued_quantity) filter (where b.status = 'posted'), 0)::numeric(20,6) as emission_eligible_quantity
               from {ValuationLinesTableName} v
               join bills b
                 on b.company_id = v.company_id
                and b.id = v.bill_id
               where v.company_id = @company_id
                 and v.receipt_id = any(@receipt_document_ids)
               group by v.receipt_id
               """
            : """
              select
                null::uuid as receipt_document_id,
                0::numeric(20,6) as valuation_backed_quantity,
                0::numeric(20,6) as emission_eligible_quantity
              where false
              """;
        var emissionGroupsSql = hasEmissionLines
            ? $"""
               select
                 e.receipt_id as receipt_document_id,
                 count(*)::int as emission_line_count,
                 coalesce(sum(e.emitted_quantity), 0)::numeric(20,6) as emitted_quantity,
                 coalesce(sum(e.emitted_cost_base), 0)::numeric(20,6) as emitted_cost_base,
                 max(e.emitted_at) as last_emitted_at
               from {EmissionLinesTableName} e
               where e.company_id = @company_id
                 and e.receipt_id = any(@receipt_document_ids)
               group by e.receipt_id
               """
            : """
              select
                null::uuid as receipt_document_id,
                0 as emission_line_count,
                0::numeric(20,6) as emitted_quantity,
                0::numeric(20,6) as emitted_cost_base,
                null::timestamptz as last_emitted_at
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
            valuation_groups as (
              {valuationGroupsSql}
            ),
            emission_groups as (
              {emissionGroupsSql}
            )
            select
              rr.receipt_document_id,
              coalesce(ag.activated_quantity, 0)::numeric(20,6) as activated_quantity,
              coalesce(vg.valuation_backed_quantity, 0)::numeric(20,6) as valuation_backed_quantity,
              coalesce(vg.emission_eligible_quantity, 0)::numeric(20,6) as emission_eligible_quantity,
              coalesce(eg.emission_line_count, 0) as emission_line_count,
              coalesce(eg.emitted_quantity, 0)::numeric(20,6) as emitted_quantity,
              coalesce(eg.emitted_cost_base, 0)::numeric(20,6) as emitted_cost_base,
              eg.last_emitted_at
            from requested_receipts rr
            left join activation_groups ag
              on ag.receipt_document_id = rr.receipt_document_id
            left join valuation_groups vg
              on vg.receipt_document_id = rr.receipt_document_id
            left join emission_groups eg
              on eg.receipt_document_id = rr.receipt_document_id;
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("receipt_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = receiptDocumentIds
        });
        command.Parameters.AddWithValue("company_id", companyId);

        var summaries = new Dictionary<Guid, ReceiptInventoryCostLayerEmissionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var receiptDocumentId = reader.GetGuid(reader.GetOrdinal("receipt_document_id"));
            var activatedQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("activated_quantity")));
            var valuationBackedQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("valuation_backed_quantity")));
            var emissionEligibleQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("emission_eligible_quantity")));
            var emittedQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("emitted_quantity")));
            var emissionStatus = ReceiptInventoryCostLayerEmissionStatusPolicy.Resolve(
                activatedQuantity,
                valuationBackedQuantity,
                emissionEligibleQuantity,
                emittedQuantity);

            summaries[receiptDocumentId] = new ReceiptInventoryCostLayerEmissionSummary(
                receiptDocumentId,
                emissionStatus,
                activatedQuantity,
                valuationBackedQuantity,
                emissionEligibleQuantity,
                emittedQuantity,
                Round6(Math.Max(0m, emissionEligibleQuantity - emittedQuantity)),
                reader.GetInt32(reader.GetOrdinal("emission_line_count")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("emitted_cost_base"))),
                reader.IsDBNull(reader.GetOrdinal("last_emitted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_emitted_at")));
        }

        return summaries;
    }

    private static async Task<ReceiptInventoryCostLayerEmissionReconciliationSummary?> LoadReceiptCostLayerEmissionReconciliationSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        bool hasActivationLines,
        bool hasEmissionLines,
        CancellationToken cancellationToken)
    {
        var summaries = await LoadReceiptCostLayerEmissionReconciliationSummariesAsync(
            connection,
            transaction,
            companyId,
            [receiptDocumentId],
            hasActivationLines,
            hasEmissionLines,
            cancellationToken);
        return summaries.TryGetValue(receiptDocumentId, out var summary) ? summary : null;
    }

    private static async Task<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionReconciliationSummary>> LoadReceiptCostLayerEmissionReconciliationSummariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid[] receiptDocumentIds,
        bool hasActivationLines,
        bool hasEmissionLines,
        CancellationToken cancellationToken)
    {
        var activationLinesSql = hasActivationLines
            ? $"""
               select
                 a.receipt_id as receipt_document_id,
                 a.inventory_document_id,
                 a.inventory_document_line_id
               from {ActivationLinesTableName} a
               where a.company_id = @company_id
                 and a.receipt_id = any(@receipt_document_ids)
               """
            : """
              select
                null::uuid as receipt_document_id,
                null::uuid as inventory_document_id,
                null::uuid as inventory_document_line_id
              where false
              """;
        var emissionGroupsSql = hasEmissionLines
            ? $"""
               select
                 e.receipt_id as receipt_document_id,
                 count(*)::int as emission_line_count,
                 coalesce(sum(e.emitted_quantity), 0)::numeric(20,6) as emitted_quantity,
                 coalesce(sum(e.emitted_cost_base), 0)::numeric(20,6) as emitted_cost_base,
                 count(*) filter (where cl.id is null)::int as missing_cost_layer_count,
                 coalesce(sum(cl.original_qty), 0)::numeric(20,6) as emitted_layer_quantity,
                 coalesce(sum(round(cl.original_qty * cl.unit_cost_base, 6)), 0)::numeric(20,6) as emitted_layer_original_cost_base,
                 max(e.emitted_at) as last_emitted_at
               from {EmissionLinesTableName} e
               left join inventory_cost_layers cl
                 on cl.company_id = e.company_id
                and cl.id = e.cost_layer_id
               where e.company_id = @company_id
                 and e.receipt_id = any(@receipt_document_ids)
               group by e.receipt_id
               """
            : """
              select
                null::uuid as receipt_document_id,
                0 as emission_line_count,
                0::numeric(20,6) as emitted_quantity,
                0::numeric(20,6) as emitted_cost_base,
                0 as missing_cost_layer_count,
                0::numeric(20,6) as emitted_layer_quantity,
                0::numeric(20,6) as emitted_layer_original_cost_base,
                null::timestamptz as last_emitted_at
              where false
              """;
        var costLayerGroupsSql = hasActivationLines
            ? $"""
               select
                 al.receipt_document_id,
                 count(cl.id)::int as cost_layer_count,
                 coalesce(sum(cl.original_qty), 0)::numeric(20,6) as cost_layer_quantity,
                 coalesce(sum(round(cl.original_qty * cl.unit_cost_base, 6)), 0)::numeric(20,6) as cost_layer_original_cost_base,
                 count(cl.id) filter (where e.id is null)::int as orphan_cost_layer_count
               from activation_lines al
               join inventory_cost_layers cl
                 on cl.company_id = @company_id
                and cl.source_document_id = al.inventory_document_id
                and cl.source_document_line_id = al.inventory_document_line_id
               left join {EmissionLinesTableName} e
                 on e.company_id = @company_id
                and e.cost_layer_id = cl.id
               group by al.receipt_document_id
               """
            : """
              select
                null::uuid as receipt_document_id,
                0 as cost_layer_count,
                0::numeric(20,6) as cost_layer_quantity,
                0::numeric(20,6) as cost_layer_original_cost_base,
                0 as orphan_cost_layer_count
              where false
              """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with requested_receipts as (
              select unnest(@receipt_document_ids::uuid[]) as receipt_document_id
            ),
            activation_lines as (
              {activationLinesSql}
            ),
            emission_groups as (
              {emissionGroupsSql}
            ),
            cost_layer_groups as (
              {costLayerGroupsSql}
            )
            select
              rr.receipt_document_id,
              coalesce(eg.emission_line_count, 0) as emission_line_count,
              coalesce(clg.cost_layer_count, 0) as cost_layer_count,
              coalesce(eg.missing_cost_layer_count, 0) as missing_cost_layer_count,
              coalesce(clg.orphan_cost_layer_count, 0) as orphan_cost_layer_count,
              coalesce(eg.emitted_quantity, 0)::numeric(20,6) as emitted_quantity,
              coalesce(clg.cost_layer_quantity, 0)::numeric(20,6) as cost_layer_quantity,
              coalesce(eg.emitted_cost_base, 0)::numeric(20,6) as emitted_cost_base,
              coalesce(clg.cost_layer_original_cost_base, 0)::numeric(20,6) as cost_layer_original_cost_base,
              eg.last_emitted_at
            from requested_receipts rr
            left join emission_groups eg
              on eg.receipt_document_id = rr.receipt_document_id
            left join cost_layer_groups clg
              on clg.receipt_document_id = rr.receipt_document_id;
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("receipt_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = receiptDocumentIds
        });
        command.Parameters.AddWithValue("company_id", companyId);

        var summaries = new Dictionary<Guid, ReceiptInventoryCostLayerEmissionReconciliationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var receiptDocumentId = reader.GetGuid(reader.GetOrdinal("receipt_document_id"));
            var emissionLineCount = reader.GetInt32(reader.GetOrdinal("emission_line_count"));
            var costLayerCount = reader.GetInt32(reader.GetOrdinal("cost_layer_count"));
            var missingCostLayerCount = reader.GetInt32(reader.GetOrdinal("missing_cost_layer_count"));
            var orphanCostLayerCount = reader.GetInt32(reader.GetOrdinal("orphan_cost_layer_count"));
            var emittedQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("emitted_quantity")));
            var costLayerQuantity = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("cost_layer_quantity")));
            var emittedCostBase = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("emitted_cost_base")));
            var costLayerOriginalCostBase = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("cost_layer_original_cost_base")));
            var reconciliationStatus = ReceiptInventoryCostLayerEmissionReconciliationStatusPolicy.Resolve(
                emissionLineCount,
                costLayerCount,
                missingCostLayerCount,
                orphanCostLayerCount,
                emittedQuantity,
                costLayerQuantity,
                emittedCostBase,
                costLayerOriginalCostBase);

            summaries[receiptDocumentId] = new ReceiptInventoryCostLayerEmissionReconciliationSummary(
                receiptDocumentId,
                reconciliationStatus,
                emissionLineCount,
                costLayerCount,
                missingCostLayerCount,
                orphanCostLayerCount,
                emittedQuantity,
                costLayerQuantity,
                emittedCostBase,
                costLayerOriginalCostBase,
                reader.IsDBNull(reader.GetOrdinal("last_emitted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_emitted_at")));
        }

        return summaries;
    }

    private async Task EnsureSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                alter table inventory_cost_layers
                  add column if not exists source_document_line_id uuid null references inventory_document_lines(id);

                create table if not exists {EmissionLinesTableName} (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  receipt_id uuid not null,
                  receipt_line_number integer not null,
                  valuation_line_id uuid not null,
                  cost_layer_id uuid not null references inventory_cost_layers(id) on delete cascade,
                  inventory_document_id uuid not null references inventory_documents(id) on delete cascade,
                  inventory_document_line_id uuid not null references inventory_document_lines(id) on delete cascade,
                  source_ledger_entry_id uuid null references inventory_ledger_entries(id) on delete set null,
                  bill_id uuid not null,
                  bill_line_number integer not null,
                  item_id uuid not null references inventory_items(id) on delete cascade,
                  warehouse_id uuid not null references inventory_warehouses(id) on delete cascade,
                  uom_code text not null,
                  emitted_quantity numeric(20, 6) not null,
                  emitted_cost_base numeric(20, 6) not null,
                  emitted_by_user_id uuid not null,
                  emitted_at timestamptz not null default now()
                );

                create unique index if not exists ux_receipt_inventory_cost_layer_emission_lines_valuation
                  on {EmissionLinesTableName} (company_id, valuation_line_id);

                create index if not exists ix_receipt_inventory_cost_layer_emission_lines_receipt
                  on {EmissionLinesTableName} (company_id, receipt_id, emitted_at desc);

                create index if not exists ix_receipt_inventory_cost_layer_emission_lines_bill
                  on {EmissionLinesTableName} (company_id, bill_id, bill_line_number);
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

    private static ReceiptInventoryCostLayerEmissionSummary BuildEmptySummary(Guid receiptDocumentId) =>
        new(
            receiptDocumentId,
            ReceiptInventoryCostLayerEmissionStatusPolicy.NoQuantityActivation,
            0m,
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
