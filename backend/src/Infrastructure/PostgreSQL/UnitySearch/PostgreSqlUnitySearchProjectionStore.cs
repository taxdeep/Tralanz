using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.PostgreSQL.UnitySearch;

public sealed class PostgreSqlUnitySearchProjectionStore(
    PostgreSqlConnectionFactory connections,
    ILogger<PostgreSqlUnitySearchProjectionStore> logger) : IUnitySearchProjectionStore
{
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _companyRefreshTimestamps = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _companyLocks = new();
    private int _schemaEnsured;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaEnsured) == 1)
        {
            return;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table if not exists search_documents (
              company_id uuid not null,
              entity_type text not null,
              source_id uuid not null,
              group_key text not null,
              primary_text text not null,
              secondary_text text not null default '',
              search_text text not null,
              search_vector tsvector null,
              exact_code_norm text not null default '',
              navigation_href text not null default '',
              metadata_json jsonb not null default '{}'::jsonb,
              effective_date date null,
              amount numeric(18, 6) null,
              is_active boolean not null default true,
              is_voided boolean not null default false,
              rank_boost numeric(18, 6) not null default 0,
              version bigint not null default 1,
              updated_at timestamptz not null default now(),
              primary key (company_id, entity_type, source_id)
            );

            create index if not exists ix_search_documents_company_group
              on search_documents (company_id, group_key, entity_type);

            create index if not exists ix_search_documents_company_exact_code
              on search_documents (company_id, exact_code_norm);

            create index if not exists ix_search_documents_search_vector
              on search_documents using gin (search_vector);

            -- Numeric-amount lookup path. The topbar's amount search resolves
            -- "11039.18" to a JE / Invoice / Bill via doc.amount; B-tree on
            -- (company_id, amount) keeps the L1 exact and L2 tolerance probes
            -- index-only even on multi-million-row charts.
            create index if not exists ix_search_documents_company_amount
              on search_documents (company_id, amount)
              where amount is not null;

            -- Per-user (per-company) priors keyed by query class. When a
            -- numeric_decimal query matches both a JE and a Bill at the
            -- same amount, the SQL ranker uses these counts as a tiebreaker.
            -- Cold-start defaults live in the SQL formula directly — this
            -- table only stores the user's *learned* deviation from default.
            create table if not exists search_query_class_priors (
              company_id uuid not null,
              user_id uuid not null,
              query_class text not null,
              entity_type text not null,
              click_count bigint not null default 0,
              last_clicked_at_utc timestamptz null,
              primary key (company_id, user_id, query_class, entity_type)
            );

            create index if not exists ix_search_query_class_priors_lookup
              on search_query_class_priors (company_id, user_id, query_class);

            create table if not exists search_recent_queries (
              company_id uuid not null,
              user_id uuid not null,
              context text not null,
              query_text text not null,
              used_at_utc timestamptz not null,
              primary key (company_id, user_id, context, query_text)
            );

            create index if not exists ix_search_recent_queries_lookup
              on search_recent_queries (company_id, user_id, context, used_at_utc desc);

            create table if not exists search_click_stats (
              company_id uuid not null,
              user_id uuid not null,
              context text not null,
              entity_type text not null,
              source_id uuid not null,
              click_count integer not null default 0,
              last_clicked_at_utc timestamptz not null,
              primary key (company_id, user_id, context, entity_type, source_id)
            );

            create index if not exists ix_search_click_stats_lookup
              on search_click_stats (company_id, user_id, context, last_clicked_at_utc desc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        Volatile.Write(ref _schemaEnsured, 1);
    }

    /// <summary>
    /// Drops the cached refresh timestamp for a company so the next
    /// search call rebuilds the projection on the spot. Backs the
    /// <see cref="InvalidateAsync"/> contract — see that doc comment
    /// for the why.
    /// </summary>
    public Task InvalidateAsync(Guid companyId, CancellationToken cancellationToken)
    {
        _companyRefreshTimestamps.TryRemove(companyId, out _);
        return Task.CompletedTask;
    }

    public async Task EnsureProjectionFreshAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (_companyRefreshTimestamps.TryGetValue(companyId, out var refreshedAt) &&
            DateTimeOffset.UtcNow - refreshedAt < RefreshWindow)
        {
            return;
        }

        var gate = _companyLocks.GetOrAdd(companyId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_companyRefreshTimestamps.TryGetValue(companyId, out refreshedAt) &&
                DateTimeOffset.UtcNow - refreshedAt < RefreshWindow)
            {
                return;
            }

            await RebuildCompanyProjectionAsync(companyId, cancellationToken);
            _companyRefreshTimestamps[companyId] = DateTimeOffset.UtcNow;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<(string EntityType, Guid SourceId), SearchDocumentDisplay>> GetDisplayNamesAsync(
        Guid companyId,
        IReadOnlyCollection<(string EntityType, Guid SourceId)> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return new Dictionary<(string EntityType, Guid SourceId), SearchDocumentDisplay>();
        }

        // Group by entity_type so each batch hits the (company_id,
        // entity_type, source_id) primary key with a single ANY() lookup
        // rather than a long OR chain.
        var byType = keys
            .GroupBy(k => k.EntityType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(k => k.SourceId).Distinct().ToArray(), StringComparer.Ordinal);

        var result = new Dictionary<(string EntityType, Guid SourceId), SearchDocumentDisplay>(keys.Count);
        await using var connection = await connections.OpenAsync(cancellationToken);

        foreach (var (entityType, ids) in byType)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                select source_id, primary_text, secondary_text, is_active
                  from search_documents
                 where company_id = @company_id
                   and entity_type = @entity_type
                   and source_id = ANY(@ids);
                """;
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("entity_type", entityType);
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            {
                Value = ids,
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var sourceId = reader.GetGuid(0);
                result[(entityType, sourceId)] = new SearchDocumentDisplay(
                    PrimaryText: reader.GetString(1),
                    SecondaryText: reader.GetString(2),
                    IsActive: reader.GetBoolean(3));
            }
        }

        return result;
    }

    private async Task RebuildCompanyProjectionAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Delete is the only step we want to fail loud — without a clean
            // wipe the rebuild can leave duplicate / stale rows. Every Seed*
            // step below runs inside its own savepoint so a single bad table
            // (column drift, missing dependency) takes out *that* entity type
            // only — the rest of the index keeps working.
            await DeleteCompanyProjectionAsync(connection, transaction, companyId, cancellationToken);

            await RunSeedStepAsync(connection, transaction, companyId, "static", SeedStaticDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "customer", SeedCustomerDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "vendor", SeedVendorDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "product_service", SeedProductServiceDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "inventory_item", SeedInventoryItemDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "inventory_stock_item", SeedInventoryStockItemDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "warehouse", SeedWarehouseDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "quote",
                (c, t, id, ct) => SeedSalesCommercialDocumentsAsync(c, t, id, "quote", ct), cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "sales_order",
                (c, t, id, ct) => SeedSalesCommercialDocumentsAsync(c, t, id, "sales_order", ct), cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "purchase_order", SeedPurchaseOrderDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "invoice", SeedInvoiceDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "bill", SeedBillDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "credit_note", SeedCreditNoteDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "vendor_credit", SeedVendorCreditDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "journal_entry", SeedJournalEntryDocumentsAsync, cancellationToken);
            await RunSeedStepAsync(connection, transaction, companyId, "account", SeedAccountDocumentsAsync, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Runs a single Seed* step inside a savepoint so a SQL failure (column
    /// drift, missing FK target, etc.) rolls back just that entity type
    /// rather than the whole rebuild. Failures are logged at warning level
    /// so an operator can see which projection lost coverage; the rest of
    /// the index continues to populate.
    /// </summary>
    private async Task RunSeedStepAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        string stepName,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, CancellationToken, Task> seed,
        CancellationToken cancellationToken)
    {
        var savepoint = $"sp_{stepName}";
        await using (var begin = connection.CreateCommand())
        {
            begin.Transaction = transaction;
            begin.CommandText = $"SAVEPOINT {savepoint};";
            await begin.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await seed(connection, transaction, companyId, cancellationToken);
            await using var release = connection.CreateCommand();
            release.Transaction = transaction;
            release.CommandText = $"RELEASE SAVEPOINT {savepoint};";
            await release.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            try
            {
                await using var rollback = connection.CreateCommand();
                rollback.Transaction = transaction;
                rollback.CommandText = $"ROLLBACK TO SAVEPOINT {savepoint};";
                await rollback.ExecuteNonQueryAsync(cancellationToken);
                await using var release = connection.CreateCommand();
                release.Transaction = transaction;
                release.CommandText = $"RELEASE SAVEPOINT {savepoint};";
                await release.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception cleanupEx)
            {
                logger.LogError(cleanupEx, "Search projection savepoint cleanup failed for step '{Step}' / company {CompanyId}.", stepName, companyId);
                throw;
            }
            logger.LogWarning(ex, "Search projection step '{Step}' for company {CompanyId} failed and was skipped.", stepName, companyId);
        }
    }

    private static async Task DeleteCompanyProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from search_documents where company_id = @company_id;";
        command.Parameters.AddWithValue("company_id", companyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedStaticDocumentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        foreach (var document in BuildStaticDocuments(companyId))
        {
            await InsertStaticDocumentAsync(connection, transaction, document, cancellationToken);
        }
    }

    private static IReadOnlyList<SearchDocumentRecord> BuildStaticDocuments(Guid companyId) =>
        new[]
        {
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Quote Pipeline", "Sales quote workbench", "/documents/source-browser?sourceType=quote", 150m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Sales Orders", "Sales order workbench", "/documents/source-browser?sourceType=sales_order", 150m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Invoice Pipeline", "Customer invoice workbench", "/documents/source-browser?sourceType=invoice&counterpartyRole=customer", 150m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Purchase Orders", "Vendor procurement workbench", "/documents/source-browser?sourceType=purchase_order", 150m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Bill Pipeline", "Vendor bill workbench", "/documents/source-browser?sourceType=bill&counterpartyRole=vendor", 150m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Receive Payment", "AR settlement entry", "/ar/receive-payment", 140m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Credit Application", "AR credit settlement entry", "/ar/credit-application", 140m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Pay Bill", "AP settlement entry", "/ap/pay-bill", 140m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Vendor Credit Application", "AP credit settlement entry", "/ap/vendor-credit-application", 140m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Customer & Vendor Setup", "Counterparty setup", "/company/customer-vendor-setup", 120m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Product & Service Setup", "Catalog setup", "/company/product-service-setup", 120m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Inventory Foundation", "Inventory setup and item truth", "/company/inventory-foundation", 120m),
            BuildStatic(companyId, SearchDocumentType.JumpTo, SearchGroupKey.JumpTo, "Journal Entries", "GL workbench", "/gl/journal-entries", 130m),
            BuildStatic(companyId, SearchDocumentType.Report, SearchGroupKey.Reports, "AR Aging", "Receivable exposure report", "/ar/aging", 110m),
            BuildStatic(companyId, SearchDocumentType.Report, SearchGroupKey.Reports, "AP Aging", "Payable exposure report", "/ap/aging", 110m),
            BuildStatic(companyId, SearchDocumentType.Report, SearchGroupKey.Reports, "Transactions", "Unified transaction search workbench", "/transactions", 100m)
        };

    private static SearchDocumentRecord BuildStatic(
        Guid companyId,
        string entityType,
        string groupKey,
        string primaryText,
        string secondaryText,
        string href,
        decimal boost)
    {
        var key = $"{entityType}:{href}";
        return new SearchDocumentRecord(
            companyId,
            entityType,
            CreateDeterministicGuid(key),
            groupKey,
            primaryText,
            secondaryText,
            $"{primaryText} {secondaryText}",
            UnitySearchCanonical(key),
            href,
            JsonSerializer.Serialize(new { href }, JsonOptions),
            null,
            null,
            true,
            false,
            boost,
            1L);
    }

    private static async Task InsertStaticDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SearchDocumentRecord document,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into search_documents (
              company_id,
              entity_type,
              source_id,
              group_key,
              primary_text,
              secondary_text,
              search_text,
              search_vector,
              exact_code_norm,
              navigation_href,
              metadata_json,
              effective_date,
              amount,
              is_active,
              is_voided,
              rank_boost,
              version
            )
            values (
              @company_id,
              @entity_type,
              @source_id,
              @group_key,
              @primary_text,
              @secondary_text,
              @search_text,
              to_tsvector('simple', @search_text),
              @exact_code_norm,
              @navigation_href,
              @metadata_json::jsonb,
              @effective_date,
              @amount,
              @is_active,
              @is_voided,
              @rank_boost,
              @version
            );
            """;
        command.Parameters.AddWithValue("company_id", document.CompanyId);
        command.Parameters.AddWithValue("entity_type", document.EntityType);
        command.Parameters.AddWithValue("source_id", document.SourceId);
        command.Parameters.AddWithValue("group_key", document.GroupKey);
        command.Parameters.AddWithValue("primary_text", document.PrimaryText);
        command.Parameters.AddWithValue("secondary_text", document.SecondaryText);
        command.Parameters.AddWithValue("search_text", document.SearchText);
        command.Parameters.AddWithValue("exact_code_norm", document.ExactCodeNorm);
        command.Parameters.AddWithValue("navigation_href", document.NavigationHref);
        command.Parameters.AddWithValue("metadata_json", document.MetadataJson);
        command.Parameters.AddWithValue("effective_date", document.EffectiveDate.HasValue ? document.EffectiveDate.Value : DBNull.Value);
        command.Parameters.AddWithValue("amount", document.Amount.HasValue ? document.Amount.Value : DBNull.Value);
        command.Parameters.AddWithValue("is_active", document.IsActive);
        command.Parameters.AddWithValue("is_voided", document.IsVoided);
        command.Parameters.AddWithValue("rank_boost", document.RankBoost);
        command.Parameters.AddWithValue("version", document.Version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedCustomerDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.customers", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              c.company_id,
              'customer',
              c.id,
              'contacts',
              c.display_name,
              concat_ws(' | ', c.entity_number, coalesce(c.default_currency_code, ''), case when c.is_active then 'active' else 'inactive' end),
              concat_ws(' ', c.display_name, c.entity_number, coalesce(c.email, ''), coalesce(c.phone, ''), coalesce(c.address, '')),
              to_tsvector('simple', concat_ws(' ', c.display_name, c.entity_number, coalesce(c.email, ''), coalesce(c.phone, ''), coalesce(c.address, ''))),
              lower(coalesce(c.entity_number, '')),
              '/documents/source-browser?counterpartyRole=customer&counterpartyId=' || c.id::text,
              jsonb_build_object('currencyCode', c.default_currency_code, 'entityNumber', c.entity_number),
              null,
              null,
              c.is_active,
              false,
              30,
              1
            from customers c
            where c.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedVendorDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.vendors", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              v.company_id,
              'vendor',
              v.id,
              'contacts',
              v.display_name,
              concat_ws(' | ', v.entity_number, coalesce(v.default_currency_code, ''), case when v.is_active then 'active' else 'inactive' end),
              concat_ws(' ', v.display_name, v.entity_number, coalesce(v.email, ''), coalesce(v.phone, ''), coalesce(v.address, '')),
              to_tsvector('simple', concat_ws(' ', v.display_name, v.entity_number, coalesce(v.email, ''), coalesce(v.phone, ''), coalesce(v.address, ''))),
              lower(coalesce(v.entity_number, '')),
              '/documents/source-browser?counterpartyRole=vendor&counterpartyId=' || v.id::text,
              jsonb_build_object('currencyCode', v.default_currency_code, 'entityNumber', v.entity_number),
              null,
              null,
              v.is_active,
              false,
              30,
              1
            from vendors v
            where v.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedProductServiceDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.company_product_service_catalog", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              item.company_id,
              'product_service',
              item.id,
              'products',
              item.name,
              concat_ws(' | ', item.entity_number, initcap(item.catalog_type), case when item.is_active then 'active' else 'inactive' end),
              concat_ws(' ', item.name, item.entity_number, item.catalog_type, coalesce(item.description, '')),
              to_tsvector('simple', concat_ws(' ', item.name, item.entity_number, item.catalog_type, coalesce(item.description, ''))),
              lower(coalesce(item.entity_number, item.name)),
              '/company/product-service-setup',
              jsonb_build_object('catalogType', item.catalog_type, 'entityNumber', item.entity_number),
              null,
              null,
              item.is_active,
              false,
              25,
              1
            from company_product_service_catalog item
            where item.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedInventoryItemDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.inventory_items", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              item.company_id,
              'inventory_item',
              item.id,
              'products',
              item.item_code,
              concat_ws(' · ', item.name, item.item_kind),
              concat_ws(' ', item.name, item.item_code, item.item_kind, coalesce(item.description, ''), coalesce(item.stock_uom_code, '')),
              to_tsvector('simple', concat_ws(' ', item.name, item.item_code, item.item_kind, coalesce(item.description, ''), coalesce(item.stock_uom_code, ''))),
              lower(coalesce(item.item_code, item.name)),
              '/items',
              jsonb_build_object('itemCode', item.item_code, 'itemKind', item.item_kind, 'stockUomCode', item.stock_uom_code),
              null,
              null,
              item.is_active,
              false,
              24,
              1
            from inventory_items item
            where item.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedInventoryStockItemDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.inventory_items", cancellationToken))
        {
            return;
        }

        // Stock-only mirror of the inventory_item projection — used by Bill /
        // PO line pickers that should only surface inventory-tracked items.
        // Stock items end up indexed twice (once as 'inventory_item', once
        // as 'inventory_stock_item'); the redundancy is small (one extra
        // row per stock item) and keeps policy filtering trivial.
        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              item.company_id,
              'inventory_stock_item',
              item.id,
              'products',
              item.item_code,
              concat_ws(' · ', item.name, item.item_kind),
              concat_ws(' ', item.name, item.item_code, coalesce(item.description, ''), coalesce(item.stock_uom_code, '')),
              to_tsvector('simple', concat_ws(' ', item.name, item.item_code, coalesce(item.description, ''), coalesce(item.stock_uom_code, ''))),
              lower(coalesce(item.item_code, item.name)),
              '/items',
              jsonb_build_object('itemCode', item.item_code, 'itemKind', item.item_kind, 'stockUomCode', item.stock_uom_code),
              null,
              null,
              item.is_active,
              false,
              24,
              1
            from inventory_items item
            where item.company_id = @company_id
              and item.item_kind = 'stock';
            """,
            cancellationToken);
    }

    private static async Task SeedWarehouseDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.inventory_warehouses", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              warehouse.company_id,
              'warehouse',
              warehouse.id,
              'products',
              warehouse.name,
              concat_ws(' | ', warehouse.warehouse_code, case when warehouse.is_active then 'active' else 'inactive' end),
              concat_ws(' ', warehouse.name, warehouse.warehouse_code, coalesce(warehouse.description, '')),
              to_tsvector('simple', concat_ws(' ', warehouse.name, warehouse.warehouse_code, coalesce(warehouse.description, ''))),
              lower(coalesce(warehouse.warehouse_code, warehouse.name)),
              '/company/inventory-foundation',
              jsonb_build_object('warehouseCode', warehouse.warehouse_code, 'description', warehouse.description),
              null,
              null,
              warehouse.is_active,
              false,
              22,
              1
            from inventory_warehouses warehouse
            where warehouse.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedSalesCommercialDocumentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        string documentType,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.web_shell_sales_commercial_documents", cancellationToken))
        {
            return;
        }

        var route = documentType == "quote"
            ? "/documents/quote/draft-editor?id="
            : "/documents/sales-order/draft-editor?id=";

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            $"""
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              doc.company_id,
              '{documentType}',
              doc.id,
              'transactions',
              doc.display_number,
              concat_ws(' | ', initcap(replace(doc.document_type, '_', ' ')), doc.status, coalesce(doc.transaction_currency_code, '')),
              concat_ws(' ', doc.display_number, doc.entity_number, doc.document_type, doc.status, coalesce(doc.memo, '')),
              to_tsvector('simple', concat_ws(' ', doc.display_number, doc.entity_number, doc.document_type, doc.status, coalesce(doc.memo, ''))),
              lower(coalesce(doc.entity_number, doc.display_number)),
              '{route}' || doc.id::text,
              jsonb_build_object('status', doc.status, 'documentDate', doc.document_date, 'currencyCode', doc.transaction_currency_code),
              doc.document_date,
              null,
              true,
              false,
              36,
              1
            from web_shell_sales_commercial_documents doc
            where doc.company_id = @company_id
              and doc.document_type = '{documentType}';
            """,
            cancellationToken);
    }

    private static async Task SeedPurchaseOrderDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.purchase_orders", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              po.company_id,
              'purchase_order',
              po.id,
              'transactions',
              po.display_number,
              concat_ws(' | ', po.status, coalesce(po.transaction_currency_code, ''), coalesce(v.display_name, '')),
              concat_ws(' ', po.display_number, po.entity_number, po.status, coalesce(v.display_name, '')),
              to_tsvector('simple', concat_ws(' ', po.display_number, po.entity_number, po.status, coalesce(v.display_name, ''))),
              lower(coalesce(po.entity_number, po.display_number)),
              '/documents/purchase-order/draft-editor?id=' || po.id::text,
              jsonb_build_object('status', po.status, 'vendorId', po.vendor_id, 'vendorName', v.display_name),
              po.order_date,
              null,
              true,
              po.status in ('cancelled'),
              38,
              1
            from purchase_orders po
            left join vendors v
              on v.company_id = po.company_id
             and v.id = po.vendor_id
            where po.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedInvoiceDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.invoices", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              i.company_id,
              'invoice',
              i.id,
              'transactions',
              i.invoice_number,
              concat_ws(' | ', i.status, coalesce(i.document_currency_code, ''), coalesce(c.display_name, ''), nullif(coalesce(i.customer_po_number, ''), '')),
              concat_ws(' ', i.invoice_number, i.entity_number, i.status, coalesce(c.display_name, ''), coalesce(i.customer_po_number, '')),
              to_tsvector('simple', concat_ws(' ', i.invoice_number, i.entity_number, i.status, coalesce(c.display_name, ''), coalesce(i.customer_po_number, ''))),
              lower(coalesce(i.entity_number, i.invoice_number)),
              '/documents/invoice/draft-editor?id=' || i.id::text,
              jsonb_build_object('status', i.status, 'customerId', i.customer_id, 'customerName', c.display_name),
              i.invoice_date,
              -- Transaction-currency amount: human habit is to recall and
              -- search for the original number they typed (e.g. "100 USD"),
              -- not the base-currency converted value. Single-currency
              -- companies see no difference; multi-currency users still
              -- find the row they remember entering.
              i.total_amount,
              true,
              i.status in ('voided', 'reversed'),
              44,
              1
            from invoices i
            left join customers c
              on c.company_id = i.company_id
             and c.id = i.customer_id
            where i.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedBillDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.bills", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              b.company_id,
              'bill',
              b.id,
              'transactions',
              b.bill_number,
              concat_ws(' | ', b.status, coalesce(b.document_currency_code, ''), coalesce(v.display_name, '')),
              concat_ws(' ', b.bill_number, b.entity_number, b.status, coalesce(v.display_name, '')),
              to_tsvector('simple', concat_ws(' ', b.bill_number, b.entity_number, b.status, coalesce(v.display_name, ''))),
              lower(coalesce(b.entity_number, b.bill_number)),
              '/documents/bill/draft-editor?id=' || b.id::text,
              jsonb_build_object('status', b.status, 'vendorId', b.vendor_id, 'vendorName', v.display_name),
              b.bill_date,
              -- Transaction-currency amount; see invoice seeder for rationale.
              b.total_amount,
              true,
              b.status in ('voided', 'reversed'),
              44,
              1
            from bills b
            left join vendors v
              on v.company_id = b.company_id
             and v.id = b.vendor_id
            where b.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedCreditNoteDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.credit_notes", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              note.company_id,
              'credit_note',
              note.id,
              'transactions',
              note.credit_note_number,
              concat_ws(' | ', note.status, coalesce(note.document_currency_code, ''), coalesce(c.display_name, ''), nullif(coalesce(note.customer_po_number, ''), '')),
              concat_ws(' ', note.credit_note_number, note.entity_number, note.status, coalesce(c.display_name, ''), coalesce(note.customer_po_number, '')),
              to_tsvector('simple', concat_ws(' ', note.credit_note_number, note.entity_number, note.status, coalesce(c.display_name, ''), coalesce(note.customer_po_number, ''))),
              lower(coalesce(note.entity_number, note.credit_note_number)),
              '/documents/credit-note/draft-editor?id=' || note.id::text,
              jsonb_build_object('status', note.status, 'customerId', note.customer_id, 'customerName', c.display_name),
              note.credit_note_date,
              -- Transaction-currency amount; see invoice seeder for rationale.
              note.total_amount,
              true,
              note.status in ('voided', 'reversed'),
              40,
              1
            from credit_notes note
            left join customers c
              on c.company_id = note.company_id
             and c.id = note.customer_id
            where note.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedVendorCreditDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.vendor_credits", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              vc.company_id,
              'vendor_credit',
              vc.id,
              'transactions',
              vc.vendor_credit_number,
              concat_ws(' | ', vc.status, coalesce(vc.document_currency_code, ''), coalesce(v.display_name, '')),
              concat_ws(' ', vc.vendor_credit_number, vc.entity_number, vc.status, coalesce(v.display_name, '')),
              to_tsvector('simple', concat_ws(' ', vc.vendor_credit_number, vc.entity_number, vc.status, coalesce(v.display_name, ''))),
              lower(coalesce(vc.entity_number, vc.vendor_credit_number)),
              '/documents/vendor-credit/draft-editor?id=' || vc.id::text,
              jsonb_build_object('status', vc.status, 'vendorId', vc.vendor_id, 'vendorName', v.display_name),
              vc.vendor_credit_date,
              -- Transaction-currency amount; see invoice seeder for rationale.
              vc.total_amount,
              true,
              vc.status in ('voided', 'reversed'),
              40,
              1
            from vendor_credits vc
            left join vendors v
              on v.company_id = vc.company_id
             and v.id = vc.vendor_id
            where vc.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedJournalEntryDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.journal_entries", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              je.company_id,
              'journal_entry',
              je.id,
              'transactions',
              je.display_number,
              concat_ws(' | ', je.status, coalesce(je.source_type, ''), coalesce(je.transaction_currency_code, '')),
              concat_ws(' ', je.display_number, je.entity_number, je.status, coalesce(je.source_type, ''), coalesce(je.source_type, '')),
              to_tsvector('simple', concat_ws(' ', je.display_number, je.entity_number, je.status, coalesce(je.source_type, ''))),
              lower(coalesce(je.entity_number, je.display_number)),
              '/gl/journal-entry/review/' || je.id::text,
              jsonb_build_object('status', je.status, 'sourceType', je.source_type, 'fxSnapshotId', je.fx_rate_snapshot_id),
              je.posted_at::date,
              -- Transaction-currency debit total. journal_entries also
              -- exposes total_debit (base currency), but topbar amount
              -- search aligns with what the user typed on the JE form,
              -- not the FX-converted base value. Single-currency companies
              -- see no difference.
              je.total_tx_debit,
              true,
              je.status in ('voided', 'reversed'),
              42,
              1
            from journal_entries je
            where je.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task SeedAccountDocumentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "public.accounts", cancellationToken))
        {
            return;
        }

        await ExecuteCompanyProjectionStepAsync(
            connection,
            transaction,
            companyId,
            """
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text, secondary_text, search_text, search_vector,
              exact_code_norm, navigation_href, metadata_json, effective_date, amount, is_active, is_voided, rank_boost, version
            )
            select
              a.company_id,
              'account',
              a.id,
              'transactions',
              concat_ws(' ', a.code, a.name),
              concat_ws(' | ', a.root_type, coalesce(a.detail_type, ''), case when a.allow_manual_posting then 'manual' else 'system' end),
              concat_ws(' ', a.code, a.name, a.root_type, coalesce(a.detail_type, '')),
              to_tsvector('simple', concat_ws(' ', a.code, a.name, a.root_type, coalesce(a.detail_type, ''))),
              lower(coalesce(a.code, '')),
              '/gl/journal-entries',
              jsonb_build_object('accountCode', a.code, 'accountName', a.name, 'allowManualPosting', a.allow_manual_posting),
              null,
              null,
              a.is_active,
              false,
              28,
              1
            from accounts a
            where a.company_id = @company_id;
            """,
            cancellationToken);
    }

    private static async Task ExecuteCompanyProjectionStepAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.Parameters.AddWithValue("company_id", companyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string relationName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select to_regclass(@relation_name) is not null;";
        command.Parameters.AddWithValue("relation_name", relationName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static Guid CreateDeterministicGuid(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes);
    }

    private static string UnitySearchCanonical(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '_');
}
