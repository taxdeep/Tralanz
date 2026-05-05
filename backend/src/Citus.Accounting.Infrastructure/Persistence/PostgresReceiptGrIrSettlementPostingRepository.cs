using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresReceiptGrIrSettlementPostingRepository : IReceiptGrIrSettlementPostingRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresReceiptGrIrSettlementPostingRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<ReceiptGrIrSettlementPostingDocument> PreparePostingDocumentAsync(
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
        await EnsureSchemaAsync(scope, cancellationToken);
        await RefreshJournalStatusAsync(scope, companyId, settlementBatchId, cancellationToken);
        await AcquireSettlementPostingLockAsync(scope, companyId, settlementBatchId, cancellationToken);

        return await LoadPostingDocumentAsync(
            scope,
            companyId,
            receiptDocumentId,
            settlementBatchId,
            cancellationToken);
    }

    public async Task CompletePostingAsync(
        CompanyId companyId,
        UserId userId,
        Guid settlementBatchId,
        Guid journalEntryId,
        string journalEntryDisplayNumber,
        CancellationToken cancellationToken)
    {
        if (settlementBatchId == Guid.Empty)
        {
            throw new ArgumentException("Settlement batch id is required.", nameof(settlementBatchId));
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

        await using var command = scope.CreateCommand(
            """
            update receipt_grir_ap_settlement_batches
            set journal_status = 'posted',
                journal_entry_id = coalesce(journal_entry_id, @journal_entry_id),
                journal_entry_display_number = coalesce(journal_entry_display_number, @journal_entry_display_number),
                journal_posted_by_user_id = coalesce(journal_posted_by_user_id, @posted_by_user_id),
                journal_posted_at = coalesce(journal_posted_at, now()),
                journal_refreshed_at = now(),
                journal_blocked_reason_code = null
            where company_id = @company_id
              and id = @settlement_batch_id
              and status = 'posted'
              and journal_status in ('not_posted', 'posted');
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        command.Parameters.AddWithValue("journal_entry_display_number", journalEntryDisplayNumber.Trim());
        command.Parameters.AddWithValue("posted_by_user_id", userId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("GR/IR settlement batch was not eligible for journal completion.");
        }
    }

    private static async Task EnsureSchemaAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(scope, "receipt_grir_ap_settlement_batches", cancellationToken) ||
            !await TableExistsAsync(scope, "receipt_grir_ap_settlement_batch_lines", cancellationToken))
        {
            throw new InvalidOperationException("The GR/IR settlement execution lane must exist before settlement journal posting.");
        }

        await using var command = scope.CreateCommand(
            """
            alter table receipt_grir_ap_settlement_batches
              add column if not exists journal_status text not null default 'not_posted';

            alter table receipt_grir_ap_settlement_batches
              add column if not exists journal_entry_id uuid null references journal_entries(id) on delete set null;

            alter table receipt_grir_ap_settlement_batches
              add column if not exists journal_entry_display_number text null;

            alter table receipt_grir_ap_settlement_batches
              add column if not exists journal_posted_by_user_id uuid null;

            alter table receipt_grir_ap_settlement_batches
              add column if not exists journal_posted_at timestamptz null;

            alter table receipt_grir_ap_settlement_batches
              add column if not exists journal_refreshed_at timestamptz null;

            alter table receipt_grir_ap_settlement_batches
              add column if not exists journal_blocked_reason_code text null;
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireSettlementPostingLockAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand("select pg_advisory_xact_lock(hashtext(@lock_key));");
        command.Parameters.AddWithValue("lock_key", $"receipt-grir-ap-settlement-posting:{companyId:N}:{settlementBatchId:N}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RefreshJournalStatusAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            update receipt_grir_ap_settlement_batches batch
            set journal_status = case
                  when batch.journal_entry_id is null then 'not_posted'
                  when je.id is null then 'journal_inconsistent'
                  when je.source_type <> 'receipt_grir_ap_settlement_posting' or je.source_id <> batch.id then 'journal_inconsistent'
                  when je.status = 'posted' then 'posted'
                  else 'journal_stale'
                end,
                journal_blocked_reason_code = case
                  when batch.journal_entry_id is null then null
                  when je.id is null then 'journal_missing'
                  when je.source_type <> 'receipt_grir_ap_settlement_posting' or je.source_id <> batch.id then 'journal_source_mismatch'
                  when je.status = 'posted' then null
                  else 'journal_not_posted'
                end,
                journal_refreshed_at = now()
            from receipt_grir_ap_settlement_batches b
            left join journal_entries je
              on je.company_id = b.company_id
             and je.id = b.journal_entry_id
            where batch.company_id = b.company_id
              and batch.id = b.id
              and batch.company_id = @company_id
              and batch.id = @settlement_batch_id
              and batch.journal_entry_id is not null;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ReceiptGrIrSettlementPostingDocument> LoadPostingDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        await using var headerCommand = scope.CreateCommand(
            """
            select
              batch.id,
              batch.receipt_id,
              batch.status,
              batch.journal_status,
              batch.journal_blocked_reason_code,
              receipt.receipt_date,
              company.base_currency_code
            from receipt_grir_ap_settlement_batches batch
            join receipts receipt
              on receipt.company_id = batch.company_id
             and receipt.id = batch.receipt_id
            join companies company
              on company.id = batch.company_id
            where batch.company_id = @company_id
              and batch.id = @settlement_batch_id
              and batch.receipt_id = @receipt_id
            limit 1;
            """);
        headerCommand.Parameters.AddWithValue("company_id", companyId);
        headerCommand.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);
        headerCommand.Parameters.AddWithValue("receipt_id", receiptDocumentId);

        Guid batchId;
        string batchStatus;
        string journalStatus;
        string? journalBlockedReasonCode;
        DateOnly documentDate;
        string baseCurrencyCode;
        await using (var reader = await headerCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("GR/IR settlement batch was not found for the receipt.");
            }

            batchId = reader.GetGuid(reader.GetOrdinal("id"));
            batchStatus = reader.GetString(reader.GetOrdinal("status"));
            journalStatus = reader.GetString(reader.GetOrdinal("journal_status"));
            journalBlockedReasonCode = reader.IsDBNull(reader.GetOrdinal("journal_blocked_reason_code"))
                ? null
                : reader.GetString(reader.GetOrdinal("journal_blocked_reason_code"));
            documentDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("receipt_date"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
        }

        if (batchStatus != "posted")
        {
            throw new InvalidOperationException("Only executed GR/IR settlement batches can be posted to journal.");
        }

        if (journalStatus == ReceiptGrIrApSettlementJournalStatusPolicy.JournalInconsistent)
        {
            throw new InvalidOperationException(
                $"GR/IR settlement batch journal state is inconsistent ({journalBlockedReasonCode ?? "unknown"}). Refresh and resolve reversal/void before retrying.");
        }

        if (journalStatus == ReceiptGrIrApSettlementJournalStatusPolicy.JournalStale)
        {
            throw new InvalidOperationException(
                $"GR/IR settlement batch journal state is stale ({journalBlockedReasonCode ?? "unknown"}). Refresh and resolve reversal/void before retrying.");
        }

        if (journalStatus is not (ReceiptGrIrApSettlementJournalStatusPolicy.NotPosted or ReceiptGrIrApSettlementJournalStatusPolicy.Posted))
        {
            throw new InvalidOperationException($"GR/IR settlement batch journal status '{journalStatus}' cannot be posted.");
        }

        var lines = await LoadPostingLinesAsync(scope, companyId, settlementBatchId, cancellationToken);
        var documentStatus = journalStatus == ReceiptGrIrApSettlementJournalStatusPolicy.Posted ? "posted" : "draft";
        var displayNumber = $"GRIR-SET-{settlementBatchId:N}"[..18].ToUpperInvariant();

        return new ReceiptGrIrSettlementPostingDocument(
            batchId,
            new CompanyId(companyId),
            new EntityNumber($"EN-{displayNumber}"),
            new DocumentNumber(displayNumber),
            documentStatus,
            receiptDocumentId,
            settlementBatchId,
            documentDate,
            new CurrencyCode(baseCurrencyCode),
            lines);
    }

    private static async Task<IReadOnlyList<ReceiptGrIrSettlementPostingDocumentLine>> LoadPostingLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid settlementBatchId,
        CancellationToken cancellationToken)
    {
        // bill_amount_base computed inline using the same proportional formula
        // as PostgresReceiptGrIrApSettlementControlStore.UpsertReceiptPurchaseVarianceLinesAsync
        // (settled_quantity / bill_line_quantity * bill_line.line_amount * bill.fx_rate).
        // When bill quantity basis is missing we fall back to bill_amount = grir_amount
        // so the journal still balances at face value (zero variance) — operator
        // can fix the bill quantity later and the variance workbench will refresh.
        //
        // PPV account resolved company-wide via system_role='purchase_price_variance'
        // (seeded by StaticCoaTemplateRegistry / PlatformFirstCompanyProvisioningRepository).
        await using var command = scope.CreateCommand(
            """
            select
              row_number() over (order by settlement_line.receipt_line_number, settlement_line.bill_line_number, batch_line.id)::int as line_number,
              batch_line.id as settlement_batch_line_id,
              batch_line.settlement_line_id,
              batch_line.settled_amount_base as grir_amount_base,
              case
                when bill_line.quantity is null or round(bill_line.quantity, 6) <= 0 then batch_line.settled_amount_base
                else round(batch_line.settled_quantity * round(bill_line.line_amount * bill.fx_rate, 6) / bill_line.quantity, 6)
              end as bill_amount_base,
              posting_batch.grir_clearing_account_id,
              bill_line.expense_account_id,
              ppv.id as ppv_account_id,
              settlement_line.settlement_status
            from receipt_grir_ap_settlement_batch_lines batch_line
            join receipt_grir_ap_settlement_lines settlement_line
              on settlement_line.company_id = batch_line.company_id
             and settlement_line.id = batch_line.settlement_line_id
            join receipt_grir_bridge_posting_batch_lines bridge_posting_line
              on bridge_posting_line.company_id = batch_line.company_id
             and bridge_posting_line.bridge_line_id = batch_line.bridge_line_id
            join receipt_grir_bridge_posting_batches posting_batch
              on posting_batch.company_id = bridge_posting_line.company_id
             and posting_batch.id = bridge_posting_line.posting_batch_id
            join bills bill
              on bill.company_id = batch_line.company_id
             and bill.id = batch_line.bill_id
            join bill_lines bill_line
              on bill_line.company_id = batch_line.company_id
             and bill_line.bill_id = batch_line.bill_id
             and bill_line.line_number = settlement_line.bill_line_number
            left join accounts ppv
              on ppv.company_id = batch_line.company_id
             and ppv.system_role = 'purchase_price_variance'
             and ppv.is_active = true
            where batch_line.company_id = @company_id
              and batch_line.settlement_batch_id = @settlement_batch_id
              and posting_batch.status = 'posted'
            order by settlement_line.receipt_line_number, settlement_line.bill_line_number, batch_line.id;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);

        var lines = new List<ReceiptGrIrSettlementPostingDocumentLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var settlementStatus = reader.GetString(reader.GetOrdinal("settlement_status"));
            if (settlementStatus.StartsWith("blocked_", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("GR/IR settlement batch contains a blocked settlement line. Refresh settlement truth before journal posting.");
            }

            var lineNumber = reader.GetInt32(reader.GetOrdinal("line_number"));
            var settlementLineId = reader.GetGuid(reader.GetOrdinal("settlement_line_id"));
            var settlementBatchLineId = reader.GetGuid(reader.GetOrdinal("settlement_batch_line_id"));
            var grIrClearingAccountId = reader.GetGuid(reader.GetOrdinal("grir_clearing_account_id"));
            var expenseAccountId = reader.GetGuid(reader.GetOrdinal("expense_account_id"));
            var grIrAmountBase = reader.GetFieldValue<decimal>(reader.GetOrdinal("grir_amount_base"));
            var billAmountBase = reader.GetFieldValue<decimal>(reader.GetOrdinal("bill_amount_base"));
            Guid? ppvAccountId = reader.IsDBNull(reader.GetOrdinal("ppv_account_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("ppv_account_id"));

            // PPV account is required when there is a non-zero variance to
            // book. Without it, the variance lands silently in expense and
            // we lose the analytical signal — surface a clear error pointing
            // at the CoA seed so the operator can add the account.
            if (Math.Round(billAmountBase - grIrAmountBase, 6, MidpointRounding.ToEven) != 0m && ppvAccountId is null)
            {
                throw new InvalidOperationException(
                    "GR/IR settlement requires a Purchase Price Variance account (system_role='purchase_price_variance') in the Chart of Accounts to book a non-zero variance.");
            }

            lines.Add(new ReceiptGrIrSettlementPostingDocumentLine(
                lineNumber,
                settlementLineId,
                settlementBatchLineId,
                grIrClearingAccountId,
                expenseAccountId,
                ppvAccountId,
                $"GR/IR settlement batch line {lineNumber}",
                grIrAmountBase,
                billAmountBase));
        }

        if (lines.Count == 0)
        {
            throw new InvalidOperationException("GR/IR settlement batch does not contain any journal-postable lines.");
        }

        return lines;
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
}
