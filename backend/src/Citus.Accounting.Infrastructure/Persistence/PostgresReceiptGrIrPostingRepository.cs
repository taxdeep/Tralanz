using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresReceiptGrIrPostingRepository : IReceiptGrIrPostingRepository
{
    private const string EligibleBridgeStatus = "eligible_not_posted";
    private const string PostedBridgeStatus = "posted";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresReceiptGrIrPostingRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<ReceiptGrIrPostingDocument> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        Guid grIrClearingAccountId,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        if (grIrClearingAccountId == Guid.Empty)
        {
            throw new ArgumentException("GR/IR clearing account id is required.", nameof(grIrClearingAccountId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);
        await EnsureSchemaAsync(scope, cancellationToken);
        await AcquireReceiptPostingLockAsync(scope, companyId, receiptDocumentId, cancellationToken);
        await EnsureActiveAccountAsync(scope, companyId, grIrClearingAccountId, "GR/IR clearing account", cancellationToken);

        var existingDraft = await TryLoadExistingPostingDocumentAsync(
            scope,
            companyId,
            receiptDocumentId,
            grIrClearingAccountId,
            status: "draft",
            cancellationToken);
        if (existingDraft is not null)
        {
            return existingDraft;
        }

        var eligibleLineCount = await CountUnbatchedEligibleLinesAsync(
            scope,
            companyId,
            receiptDocumentId,
            cancellationToken);
        if (eligibleLineCount <= 0)
        {
            var existingPosted = await TryLoadExistingPostingDocumentAsync(
                scope,
                companyId,
                receiptDocumentId,
                grIrClearingAccountId,
                status: "posted",
                cancellationToken);
            if (existingPosted is not null)
            {
                return existingPosted;
            }

            throw new InvalidOperationException(
                "No eligible GR/IR bridge lines are ready for posting. Refresh the bridge and resolve blocked reconciliation before posting.");
        }

        await EnsureEligibleLinesHaveInventoryAssetAccountsAsync(scope, companyId, receiptDocumentId, cancellationToken);

        var postingBatchId = Guid.NewGuid();
        var batchIdentity = await CreatePostingBatchAsync(
            scope,
            companyId,
            userId,
            postingBatchId,
            receiptDocumentId,
            grIrClearingAccountId,
            cancellationToken);
        await AttachEligibleLinesAsync(
            scope,
            companyId,
            postingBatchId,
            receiptDocumentId,
            cancellationToken);

        return await LoadPostingDocumentAsync(
            scope,
            companyId,
            postingBatchId,
            batchIdentity.EntityNumber,
            batchIdentity.DisplayNumber,
            cancellationToken);
    }

    public async Task CompletePostingAsync(
        CompanyId companyId,
        UserId userId,
        Guid postingBatchId,
        Guid journalEntryId,
        string journalEntryDisplayNumber,
        CancellationToken cancellationToken)
    {
        if (postingBatchId == Guid.Empty)
        {
            throw new ArgumentException("Posting batch id is required.", nameof(postingBatchId));
        }

        if (journalEntryId == Guid.Empty)
        {
            throw new ArgumentException("Journal entry id is required.", nameof(journalEntryId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);
        await EnsureSchemaAsync(scope, cancellationToken);

        await using (var batchCommand = scope.CreateCommand(
                         """
                         update receipt_grir_bridge_posting_batches
                         set journal_entry_id = @journal_entry_id,
                             journal_entry_display_number = @journal_entry_display_number,
                             posted_by_user_id = @posted_by_user_id,
                             posted_at = coalesce(posted_at, now()),
                             updated_at = now()
                         where company_id = @company_id
                           and id = @posting_batch_id
                           and status = 'posted';
                         """))
        {
            batchCommand.Parameters.AddWithValue("company_id", companyId.Value);
            batchCommand.Parameters.AddWithValue("posting_batch_id", postingBatchId);
            batchCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
            batchCommand.Parameters.AddWithValue("journal_entry_display_number", journalEntryDisplayNumber.Trim());
            batchCommand.Parameters.AddWithValue("posted_by_user_id", userId.Value);

            if (await batchCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new InvalidOperationException("GR/IR posting batch was not marked posted by the posting engine.");
            }
        }

        await using var lineCommand = scope.CreateCommand(
            """
            update receipt_grir_bridge_lines bridge
            set bridge_status = 'posted',
                journal_entry_id = @journal_entry_id,
                journal_entry_display_number = @journal_entry_display_number,
                posted_by_user_id = @posted_by_user_id,
                posted_at = coalesce(bridge.posted_at, now())
            from receipt_grir_bridge_posting_batch_lines batch_line
            where batch_line.company_id = bridge.company_id
              and batch_line.bridge_line_id = bridge.id
              and batch_line.company_id = @company_id
              and batch_line.posting_batch_id = @posting_batch_id
              and bridge.bridge_status in ('eligible_not_posted', 'posted');
            """);
        lineCommand.Parameters.AddWithValue("company_id", companyId.Value);
        lineCommand.Parameters.AddWithValue("posting_batch_id", postingBatchId);
        lineCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        lineCommand.Parameters.AddWithValue("journal_entry_display_number", journalEntryDisplayNumber.Trim());
        lineCommand.Parameters.AddWithValue("posted_by_user_id", userId.Value);
        await lineCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSchemaAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(scope, "receipt_grir_bridge_lines", cancellationToken))
        {
            throw new InvalidOperationException("The receipt GR/IR bridge lane must be refreshed before GR/IR posting.");
        }

        await using var command = scope.CreateCommand(
            """
            alter table receipt_grir_bridge_lines
              add column if not exists journal_entry_id uuid null references journal_entries(id) on delete set null;

            alter table receipt_grir_bridge_lines
              add column if not exists journal_entry_display_number text null;

            alter table receipt_grir_bridge_lines
              add column if not exists posted_by_user_id char(7) null;

            alter table receipt_grir_bridge_lines
              add column if not exists posted_at timestamptz null;

            create table if not exists receipt_grir_bridge_posting_batches (
              id uuid primary key,
              company_id char(7) not null references companies(id) on delete cascade,
              receipt_id uuid not null,
              entity_number char(11) not null,
              display_number text not null,
              status text not null,
              document_date date not null,
              transaction_currency_code char(3) not null,
              base_currency_code char(3) not null,
              grir_clearing_account_id uuid not null references accounts(id),
              total_amount_base numeric(20, 6) not null,
              line_count integer not null,
              journal_entry_id uuid null references journal_entries(id) on delete set null,
              journal_entry_display_number text null,
              created_by_user_id char(7) not null,
              created_at timestamptz not null default now(),
              posted_by_user_id char(7) null,
              posted_at timestamptz null,
              updated_at timestamptz not null default now()
            );

            create unique index if not exists ux_receipt_grir_bridge_posting_batches_entity
              on receipt_grir_bridge_posting_batches (company_id, entity_number);

            create index if not exists ix_receipt_grir_bridge_posting_batches_receipt
              on receipt_grir_bridge_posting_batches (company_id, receipt_id, created_at desc);

            create table if not exists receipt_grir_bridge_posting_batch_lines (
              id uuid primary key default gen_random_uuid(),
              company_id char(7) not null references companies(id) on delete cascade,
              posting_batch_id uuid not null references receipt_grir_bridge_posting_batches(id) on delete cascade,
              bridge_line_id uuid not null references receipt_grir_bridge_lines(id) on delete cascade,
              inventory_asset_account_id uuid not null references accounts(id),
              amount_base numeric(20, 6) not null,
              created_at timestamptz not null default now()
            );

            create unique index if not exists ux_receipt_grir_bridge_posting_batch_lines_bridge
              on receipt_grir_bridge_posting_batch_lines (company_id, bridge_line_id);

            create index if not exists ix_receipt_grir_bridge_posting_batch_lines_batch
              on receipt_grir_bridge_posting_batch_lines (company_id, posting_batch_id);
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireReceiptPostingLockAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand("select pg_advisory_xact_lock(hashtext(@lock_key));");
        command.Parameters.AddWithValue("lock_key", $"receipt-grir-posting:{companyId:N}:{receiptDocumentId:N}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureActiveAccountAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid accountId,
        string label,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select exists (
              select 1
              from accounts
              where company_id = @company_id
                and id = @account_id
                and is_active = true
            );
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("account_id", accountId);
        var exists = await command.ExecuteScalarAsync(cancellationToken);
        if (exists is not true)
        {
            throw new InvalidOperationException($"{label} must be an active account in the active company.");
        }
    }

    private static async Task<int> CountUnbatchedEligibleLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select count(*)::int
            from receipt_grir_bridge_lines bridge
            where bridge.company_id = @company_id
              and bridge.receipt_id = @receipt_id
              and bridge.bridge_status = 'eligible_not_posted'
              and not exists (
                select 1
                from receipt_grir_bridge_posting_batch_lines batch_line
                where batch_line.company_id = bridge.company_id
                  and batch_line.bridge_line_id = bridge.id
              );
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task EnsureEligibleLinesHaveInventoryAssetAccountsAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select count(*)::int
            from receipt_grir_bridge_lines bridge
            join inventory_items item
              on item.company_id = bridge.company_id
             and item.id = bridge.item_id
            where bridge.company_id = @company_id
              and bridge.receipt_id = @receipt_id
              and bridge.bridge_status = 'eligible_not_posted'
              and item.default_inventory_asset_account_id is null
              and not exists (
                select 1
                from receipt_grir_bridge_posting_batch_lines batch_line
                where batch_line.company_id = bridge.company_id
                  and batch_line.bridge_line_id = bridge.id
              );
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        var missingCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        if (missingCount > 0)
        {
            throw new InvalidOperationException(
                "Every eligible GR/IR bridge line must resolve an inventory asset account from the item master before posting.");
        }
    }

    private static async Task<(string EntityNumber, string DisplayNumber)> CreatePostingBatchAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        UserId userId,
        Guid postingBatchId,
        Guid receiptDocumentId,
        Guid grIrClearingAccountId,
        CancellationToken cancellationToken)
    {
        var entityNumber = $"EN-GRIR-{postingBatchId:N}"[..18].ToUpperInvariant();
        var displayNumber = $"GRIR-{postingBatchId:N}"[..18].ToUpperInvariant();

        await using var command = scope.CreateCommand(
            """
            insert into receipt_grir_bridge_posting_batches (
              id,
              company_id,
              receipt_id,
              entity_number,
              display_number,
              status,
              document_date,
              transaction_currency_code,
              base_currency_code,
              grir_clearing_account_id,
              total_amount_base,
              line_count,
              created_by_user_id
            )
            select
              @posting_batch_id,
              r.company_id,
              r.id,
              @entity_number,
              @display_number,
              'draft',
              r.receipt_date,
              c.base_currency_code,
              c.base_currency_code,
              @grir_clearing_account_id,
              (
                select coalesce(sum(bridge.bridge_amount_base), 0)
                from receipt_grir_bridge_lines bridge
                where bridge.company_id = r.company_id
                  and bridge.receipt_id = r.id
                  and bridge.bridge_status = 'eligible_not_posted'
                  and not exists (
                    select 1
                    from receipt_grir_bridge_posting_batch_lines batch_line
                    where batch_line.company_id = bridge.company_id
                      and batch_line.bridge_line_id = bridge.id
                  )
              ),
              (
                select count(*)::int
                from receipt_grir_bridge_lines bridge
                where bridge.company_id = r.company_id
                  and bridge.receipt_id = r.id
                  and bridge.bridge_status = 'eligible_not_posted'
                  and not exists (
                    select 1
                    from receipt_grir_bridge_posting_batch_lines batch_line
                    where batch_line.company_id = bridge.company_id
                      and batch_line.bridge_line_id = bridge.id
                  )
              ),
              @created_by_user_id
            from receipts r
            join companies c
              on c.id = r.company_id
            where r.company_id = @company_id
              and r.id = @receipt_id
              and r.status = 'posted';
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("posting_batch_id", postingBatchId);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("display_number", displayNumber);
        command.Parameters.AddWithValue("grir_clearing_account_id", grIrClearingAccountId);
        command.Parameters.AddWithValue("created_by_user_id", userId);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("Receipt must exist and be posted before GR/IR posting.");
        }

        return (entityNumber, displayNumber);
    }

    private static async Task AttachEligibleLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid postingBatchId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            insert into receipt_grir_bridge_posting_batch_lines (
              company_id,
              posting_batch_id,
              bridge_line_id,
              inventory_asset_account_id,
              amount_base
            )
            select
              bridge.company_id,
              @posting_batch_id,
              bridge.id,
              item.default_inventory_asset_account_id,
              bridge.bridge_amount_base
            from receipt_grir_bridge_lines bridge
            join inventory_items item
              on item.company_id = bridge.company_id
             and item.id = bridge.item_id
            where bridge.company_id = @company_id
              and bridge.receipt_id = @receipt_id
              and bridge.bridge_status = 'eligible_not_posted'
              and item.default_inventory_asset_account_id is not null
              and not exists (
                select 1
                from receipt_grir_bridge_posting_batch_lines batch_line
                where batch_line.company_id = bridge.company_id
                  and batch_line.bridge_line_id = bridge.id
              )
            on conflict (company_id, bridge_line_id)
            do nothing;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("posting_batch_id", postingBatchId);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("No eligible GR/IR bridge lines could be attached to the posting batch.");
        }
    }

    private static async Task<ReceiptGrIrPostingDocument?> TryLoadExistingPostingDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid receiptDocumentId,
        Guid grIrClearingAccountId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select id, entity_number, display_number
            from receipt_grir_bridge_posting_batches
            where company_id = @company_id
              and receipt_id = @receipt_id
              and grir_clearing_account_id = @grir_clearing_account_id
              and status = @status
            order by created_at desc
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("grir_clearing_account_id", grIrClearingAccountId);
        command.Parameters.AddWithValue("status", status);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var batchId = reader.GetGuid(reader.GetOrdinal("id"));
        var entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
        var displayNumber = reader.GetString(reader.GetOrdinal("display_number"));
        await reader.DisposeAsync();

        return await LoadPostingDocumentAsync(scope, companyId, batchId, entityNumber, displayNumber, cancellationToken);
    }

    private static async Task<ReceiptGrIrPostingDocument> LoadPostingDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid postingBatchId,
        string entityNumber,
        string displayNumber,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              batch.id,
              batch.company_id,
              batch.receipt_id,
              batch.status,
              batch.document_date,
              batch.base_currency_code,
              batch.grir_clearing_account_id,
              batch_line.bridge_line_id,
              batch_line.inventory_asset_account_id,
              batch_line.amount_base,
              row_number() over (order by bridge.receipt_line_number, bridge.id)::int as line_number
            from receipt_grir_bridge_posting_batches batch
            join receipt_grir_bridge_posting_batch_lines batch_line
              on batch_line.company_id = batch.company_id
             and batch_line.posting_batch_id = batch.id
            join receipt_grir_bridge_lines bridge
              on bridge.company_id = batch_line.company_id
             and bridge.id = batch_line.bridge_line_id
            where batch.company_id = @company_id
              and batch.id = @posting_batch_id
            order by bridge.receipt_line_number, bridge.id;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("posting_batch_id", postingBatchId);

        Guid receiptDocumentId = Guid.Empty;
        string status = "draft";
        DateOnly documentDate = default;
        string baseCurrencyCode = string.Empty;
        Guid grIrClearingAccountId = default;
        var lines = new List<ReceiptGrIrPostingDocumentLine>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            receiptDocumentId = reader.GetGuid(reader.GetOrdinal("receipt_id"));
            status = reader.GetString(reader.GetOrdinal("status"));
            documentDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            grIrClearingAccountId = reader.GetGuid(reader.GetOrdinal("grir_clearing_account_id"));
            var lineNumber = reader.GetInt32(reader.GetOrdinal("line_number"));
            var bridgeLineId = reader.GetGuid(reader.GetOrdinal("bridge_line_id"));
            var inventoryAssetAccountId = reader.GetGuid(reader.GetOrdinal("inventory_asset_account_id"));
            var amountBase = reader.GetFieldValue<decimal>(reader.GetOrdinal("amount_base"));

            lines.Add(new ReceiptGrIrPostingDocumentLine(
                lineNumber,
                bridgeLineId,
                inventoryAssetAccountId,
                grIrClearingAccountId,
                $"Receipt GR/IR bridge line {lineNumber}",
                amountBase));
        }

        if (lines.Count == 0)
        {
            throw new InvalidOperationException("GR/IR posting batch does not contain any bridge lines.");
        }

        return new ReceiptGrIrPostingDocument(
            postingBatchId,
            CompanyId.Parse(companyId.ToString()),
            EntityNumber.Parse(entityNumber),
            new DocumentNumber(displayNumber),
            status,
            receiptDocumentId,
            documentDate,
            new CurrencyCode(baseCurrencyCode),
            grIrClearingAccountId,
            lines);
    }

    private static async Task<bool> TableExistsAsync(
        PostgresCommandScope scope,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand("select to_regclass(@table_name) is not null;");
        command.Parameters.AddWithValue("table_name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }
}
