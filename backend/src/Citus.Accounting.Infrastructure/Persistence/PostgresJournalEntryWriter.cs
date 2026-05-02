using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Domain.Journal;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresJournalEntryWriter : IJournalEntryWriter
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresJournalEntryWriter(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<JournalEntryWriteResult> WriteAsync(
        JournalEntryDraft draft,
        PostingContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);

        EnsureDraftIsBalanced(draft);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);
        await EnsureJournalEntryLineAuditColumnsAsync(scope, cancellationToken);

        var idempotencyKey = context.IdempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            idempotencyKey = $"{draft.SourceType}:{draft.SourceId}";
        }

        var existing = await TryFindExistingByIdempotencyKeyAsync(
            scope,
            context.CompanyId.Value,
            idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        existing = await TryFindExistingBySourceAsync(
            scope,
            context.CompanyId.Value,
            draft.SourceType,
            draft.SourceId,
            cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var postingDate = draft.FxSnapshot.RequestedDate;
        await EnsurePostingPeriodOpenAsync(
            scope,
            context.CompanyId.Value,
            postingDate,
            cancellationToken);

        var postedAt = context.RequestedAt.UtcDateTime;

        var claimed = await TryMarkSourcePostedAsync(
            scope,
            context.CompanyId.Value,
            draft.SourceType,
            draft.SourceId,
            postedAt,
            cancellationToken);

        if (!claimed)
        {
            existing = await TryFindExistingBySourceAsync(
                scope,
                context.CompanyId.Value,
                draft.SourceType,
                draft.SourceId,
                cancellationToken);

            if (existing is not null)
            {
                return existing;
            }

            throw new InvalidOperationException(
                $"Source document '{draft.SourceType}' could not be marked as posted. The source state no longer matches a draft posting flow.");
        }

        var entityNumber = await PostgresNumberingSequences.ReserveAsync(
            scope,
            context.CompanyId.Value,
            $"entity-number:journal-entry:{postedAt:yyyy}",
            $"EN{postedAt:yyyy}",
            padding: 8,
            cancellationToken);

        var displayNumber = await PostgresNumberingSequences.ReserveAsync(
            scope,
            context.CompanyId.Value,
            "journal-entry-display",
            "JE-",
            padding: 6,
            cancellationToken);

        var journalEntryId = Guid.NewGuid();
        var totalTxDebit = Round6(draft.Lines.Sum(static line => line.TxDebit));
        var totalTxCredit = Round6(draft.Lines.Sum(static line => line.TxCredit));
        var totalDebit = Round6(draft.Lines.Sum(static line => line.Debit));
        var totalCredit = Round6(draft.Lines.Sum(static line => line.Credit));

        await using (var entryCommand = scope.CreateCommand(
                         """
                         insert into journal_entries (
                           id,
                           company_id,
                           entity_number,
                           display_number,
                           status,
                           source_type,
                           source_id,
                           transaction_currency_code,
                           base_currency_code,
                           exchange_rate,
                           exchange_rate_date,
                           exchange_rate_source,
                           fx_rate_snapshot_id,
                           total_tx_debit,
                           total_tx_credit,
                           total_debit,
                           total_credit,
                           posting_run_id,
                           idempotency_key,
                           posted_at,
                           created_by_user_id
                         )
                         values (
                           @id,
                           @company_id,
                           @entity_number,
                           @display_number,
                           'posted',
                           @source_type,
                           @source_id,
                           @transaction_currency_code,
                           @base_currency_code,
                           @exchange_rate,
                           @exchange_rate_date,
                           @exchange_rate_source,
                           @fx_rate_snapshot_id,
                           @total_tx_debit,
                           @total_tx_credit,
                           @total_debit,
                           @total_credit,
                           @posting_run_id,
                           @idempotency_key,
                           @posted_at,
                           @created_by_user_id
                         );
                         """))
        {
            entryCommand.Parameters.AddWithValue("id", journalEntryId);
            entryCommand.Parameters.AddWithValue("company_id", context.CompanyId.Value);
            entryCommand.Parameters.AddWithValue("entity_number", entityNumber);
            entryCommand.Parameters.AddWithValue("display_number", displayNumber);
            entryCommand.Parameters.AddWithValue("source_type", draft.SourceType);
            entryCommand.Parameters.AddWithValue("source_id", draft.SourceId);
            entryCommand.Parameters.AddWithValue("transaction_currency_code", draft.TransactionCurrencyCode.Value);
            entryCommand.Parameters.AddWithValue("base_currency_code", draft.BaseCurrencyCode.Value);
            entryCommand.Parameters.AddWithValue("exchange_rate", draft.FxSnapshot.Rate);
            entryCommand.Parameters.AddWithValue("exchange_rate_date", draft.FxSnapshot.EffectiveDate);
            entryCommand.Parameters.AddWithValue("exchange_rate_source", draft.FxSnapshot.SourceSemantics);
            entryCommand.Parameters.AddWithValue(
                "fx_rate_snapshot_id",
                draft.FxSnapshot.SnapshotId == Guid.Empty
                    ? DBNull.Value
                    : (object)draft.FxSnapshot.SnapshotId);
            entryCommand.Parameters.AddWithValue("total_tx_debit", totalTxDebit);
            entryCommand.Parameters.AddWithValue("total_tx_credit", totalTxCredit);
            entryCommand.Parameters.AddWithValue("total_debit", totalDebit);
            entryCommand.Parameters.AddWithValue("total_credit", totalCredit);
            entryCommand.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
            entryCommand.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            entryCommand.Parameters.AddWithValue("posted_at", postedAt);
            entryCommand.Parameters.AddWithValue("created_by_user_id", context.UserId.Value);
            await entryCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in draft.Lines)
        {
            var journalEntryLineId = Guid.NewGuid();

            await using (var lineCommand = scope.CreateCommand(
                             """
                             insert into journal_entry_lines (
                               id,
                               company_id,
                               journal_entry_id,
                               line_number,
                               account_id,
                               description,
                               party_type,
                               party_id,
                               tx_debit,
                               tx_credit,
                               debit,
                               credit,
                               tax_component_type,
                               control_role,
                               posting_role,
                               source_line_number
                             )
                             values (
                               @id,
                               @company_id,
                               @journal_entry_id,
                               @line_number,
                               @account_id,
                               @description,
                               @party_type,
                               @party_id,
                               @tx_debit,
                               @tx_credit,
                               @debit,
                               @credit,
                               @tax_component_type,
                               @control_role,
                               @posting_role,
                               @source_line_number
                             );
                             """))
            {
                lineCommand.Parameters.AddWithValue("id", journalEntryLineId);
                lineCommand.Parameters.AddWithValue("company_id", context.CompanyId.Value);
                lineCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
                lineCommand.Parameters.AddWithValue("line_number", line.LineNumber);
                lineCommand.Parameters.AddWithValue("account_id", line.AccountId);
                lineCommand.Parameters.AddWithValue(
                    "description",
                    string.IsNullOrWhiteSpace(line.Description)
                        ? DBNull.Value
                        : (object)line.Description);
                lineCommand.Parameters.AddWithValue("party_type", DBNull.Value);
                lineCommand.Parameters.AddWithValue(
                    "party_id",
                    line.PartyId.HasValue ? (object)line.PartyId.Value : DBNull.Value);
                lineCommand.Parameters.AddWithValue("tx_debit", Round6(line.TxDebit));
                lineCommand.Parameters.AddWithValue("tx_credit", Round6(line.TxCredit));
                lineCommand.Parameters.AddWithValue("debit", Round6(line.Debit));
                lineCommand.Parameters.AddWithValue("credit", Round6(line.Credit));
                lineCommand.Parameters.AddWithValue(
                    "tax_component_type",
                    string.IsNullOrWhiteSpace(line.TaxComponentType)
                        ? DBNull.Value
                        : (object)line.TaxComponentType);
                lineCommand.Parameters.AddWithValue(
                    "control_role",
                    string.IsNullOrWhiteSpace(line.ControlRole)
                        ? DBNull.Value
                        : (object)line.ControlRole);
                lineCommand.Parameters.AddWithValue(
                    "posting_role",
                    string.IsNullOrWhiteSpace(line.PostingRole)
                        ? DBNull.Value
                        : (object)line.PostingRole);
                lineCommand.Parameters.AddWithValue(
                    "source_line_number",
                    line.SourceLineNumber.HasValue ? (object)line.SourceLineNumber.Value : DBNull.Value);
                await lineCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var ledgerCommand = scope.CreateCommand(
                """
                insert into ledger_entries (
                  id,
                  company_id,
                  journal_entry_id,
                  journal_entry_line_id,
                  posting_date,
                  account_id,
                  debit,
                  credit,
                  transaction_currency_code,
                  tx_debit,
                  tx_credit
                )
                values (
                  @id,
                  @company_id,
                  @journal_entry_id,
                  @journal_entry_line_id,
                  @posting_date,
                  @account_id,
                  @debit,
                  @credit,
                  @transaction_currency_code,
                  @tx_debit,
                  @tx_credit
                );
                """);

            ledgerCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            ledgerCommand.Parameters.AddWithValue("company_id", context.CompanyId.Value);
            ledgerCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
            ledgerCommand.Parameters.AddWithValue("journal_entry_line_id", journalEntryLineId);
            ledgerCommand.Parameters.AddWithValue("posting_date", postingDate);
            ledgerCommand.Parameters.AddWithValue("account_id", line.AccountId);
            ledgerCommand.Parameters.AddWithValue("debit", Round6(line.Debit));
            ledgerCommand.Parameters.AddWithValue("credit", Round6(line.Credit));
            ledgerCommand.Parameters.AddWithValue("transaction_currency_code", draft.TransactionCurrencyCode.Value);
            ledgerCommand.Parameters.AddWithValue("tx_debit", Round6(line.TxDebit));
            ledgerCommand.Parameters.AddWithValue("tx_credit", Round6(line.TxCredit));
            await ledgerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return new JournalEntryWriteResult(journalEntryId, displayNumber);
    }

    private static void EnsureDraftIsBalanced(JournalEntryDraft draft)
    {
        if (Round6(draft.TotalDebit) != Round6(draft.TotalCredit))
        {
            throw new InvalidOperationException("Journal entry draft is not balanced in base currency.");
        }
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private static async Task EnsureJournalEntryLineAuditColumnsAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            alter table journal_entry_lines
              add column if not exists posting_role text null,
              add column if not exists source_line_number integer null;
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<JournalEntryWriteResult?> TryFindExistingByIdempotencyKeyAsync(
        PostgresCommandScope scope,
        Guid companyId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select id, display_number
            from journal_entries
            where company_id = @company_id
              and idempotency_key = @idempotency_key
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new JournalEntryWriteResult(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("display_number")));
    }

    private static async Task<JournalEntryWriteResult?> TryFindExistingBySourceAsync(
        PostgresCommandScope scope,
        Guid companyId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select id, display_number
            from journal_entries
            where company_id = @company_id
              and source_type = @source_type
              and source_id = @source_id
              and status = 'posted'
            order by created_at desc
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new JournalEntryWriteResult(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("display_number")));
    }

    private static async Task EnsurePostingPeriodOpenAsync(
        PostgresCommandScope scope,
        Guid companyId,
        DateOnly postingDate,
        CancellationToken cancellationToken)
    {
        if (!await BookGovernanceTablesExistAsync(scope, cancellationToken))
        {
            return;
        }

        await using var command = scope.CreateCommand(
            """
            select
              s.signal_date,
              s.reference_label
            from company_books b
            inner join company_book_governance_signals s
              on s.company_id = b.company_id
             and s.company_book_id = b.id
             and s.signal_type = 'closed_period'
             and s.signal_date >= @posting_date
            where b.company_id = @company_id
              and b.is_active = true
              and b.is_primary = true
              and b.effective_from <= @posting_date
            order by s.signal_date asc, s.created_at asc, s.id asc
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("posting_date", postingDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var closedThrough = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("signal_date"));
        var referenceLabel = reader.IsDBNull(reader.GetOrdinal("reference_label"))
            ? "closed period"
            : reader.GetString(reader.GetOrdinal("reference_label"));
        throw new InvalidOperationException(
            $"Posting date {postingDate:yyyy-MM-dd} is locked by {referenceLabel} through {closedThrough:yyyy-MM-dd}.");
    }

    private static async Task<bool> BookGovernanceTablesExistAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select to_regclass('public.company_books') is not null
               and to_regclass('public.company_book_governance_signals') is not null;
            """);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<bool> TryMarkSourcePostedAsync(
        PostgresCommandScope scope,
        Guid companyId,
        string sourceType,
        Guid sourceId,
        DateTime postedAt,
        CancellationToken cancellationToken)
    {
        var sql = sourceType switch
        {
            "manual_journal" => """
                                update manual_journal_documents
                                set status = 'posted',
                                    posted_at = @posted_at,
                                    updated_at = now()
                                where company_id = @company_id
                                  and id = @source_id
                                  and status = 'draft';
                                """,
            "invoice" => """
                         update invoices
                         set status = 'posted',
                             posted_at = @posted_at,
                             updated_at = now()
                         where company_id = @company_id
                           and id = @source_id
                           and status in ('draft', 'issued');
                         """,
            "sales_receipt" => """
                                update sales_receipts
                                set status = 'posted',
                                    posted_at = @posted_at,
                                    updated_at = now()
                                where company_id = @company_id
                                  and id = @source_id
                                  and status = 'draft';
                                """,
            "refund_receipt" => """
                                 update refund_receipts
                                 set status = 'posted',
                                     posted_at = @posted_at,
                                     updated_at = now()
                                 where company_id = @company_id
                                   and id = @source_id
                                   and status = 'draft';
                                 """,
            "credit_note" => """
                              update credit_notes
                              set status = 'posted',
                                  posted_at = @posted_at,
                                  updated_at = now()
                              where company_id = @company_id
                                and id = @source_id
                                and status in ('draft', 'issued');
                              """,
            "bill" => """
                      update bills
                      set status = 'posted',
                          posted_at = @posted_at,
                          updated_at = now()
                      where company_id = @company_id
                        and id = @source_id
                        and status = 'draft';
                      """,
            "vendor_credit" => """
                                update vendor_credits
                                set status = 'posted',
                                    posted_at = @posted_at,
                                    updated_at = now()
                                where company_id = @company_id
                                and id = @source_id
                                and status = 'draft';
                                """,
            "receive_payment" => """
                                 update receive_payments
                                 set status = 'posted',
                                     posted_at = @posted_at,
                                     updated_at = now()
                                 where company_id = @company_id
                                  and id = @source_id
                                  and status = 'draft';
                                 """,
            "credit_application" => """
                                     update credit_applications
                                     set status = 'posted',
                                         posted_at = @posted_at,
                                         updated_at = now()
                                     where company_id = @company_id
                                       and id = @source_id
                                       and status = 'draft';
                                     """,
            "pay_bill" => """
                          update pay_bills
                          set status = 'posted',
                              posted_at = @posted_at,
                              updated_at = now()
                          where company_id = @company_id
                            and id = @source_id
                            and status = 'draft';
                          """,
            "vendor_credit_application" => """
                                            update vendor_credit_applications
                                            set status = 'posted',
                                                posted_at = @posted_at,
                                                updated_at = now()
                                            where company_id = @company_id
                                              and id = @source_id
                                              and status = 'draft';
                                            """,
            "fx_revaluation" => """
                                update fx_revaluation_batches
                                set status = 'posted',
                                    posted_at = @posted_at,
                                    updated_at = now()
                                where company_id = @company_id
                                  and id = @source_id
                                  and status = 'draft';
                                """,
            "receipt_grir_bridge_posting" => """
                                             update receipt_grir_bridge_posting_batches
                                             set status = 'posted',
                                                 posted_at = @posted_at,
                                                 updated_at = now()
                                             where company_id = @company_id
                                               and id = @source_id
                                               and status = 'draft';
                                             """,
            "receipt_grir_ap_settlement_posting" => """
                                                     update receipt_grir_ap_settlement_batches
                                                     set journal_status = 'posted',
                                                         journal_posted_at = coalesce(journal_posted_at, @posted_at),
                                                         journal_refreshed_at = now(),
                                                         journal_blocked_reason_code = null
                                                     where company_id = @company_id
                                                       and id = @source_id
                                                       and status = 'posted'
                                                       and journal_status = 'not_posted';
                                                     """,
            "ar_open_item_adjustment" or "ap_open_item_adjustment" => """
                                                                      insert into audit_logs (
                                                                        id,
                                                                        company_id,
                                                                        actor_type,
                                                                        actor_id,
                                                                        entity_type,
                                                                        entity_id,
                                                                        action,
                                                                        payload
                                                                      )
                                                                      select
                                                                        @claim_id,
                                                                        @company_id,
                                                                        'system',
                                                                        null,
                                                                        'open_item_adjustment_request',
                                                                        @source_id,
                                                                        'open_item_adjustment_execution_requested',
                                                                        jsonb_build_object(
                                                                          'RequestId', @source_id::text,
                                                                          'OpenItemId', requested.payload ->> 'OpenItemId',
                                                                          'OpenItemType', requested.payload ->> 'OpenItemType',
                                                                          'SourceType', @source_type,
                                                                          'PostedAt', @posted_at
                                                                        )
                                                                      from audit_logs requested
                                                                      where requested.company_id = @company_id
                                                                        and requested.entity_type = 'open_item_adjustment_request'
                                                                        and requested.entity_id = @source_id
                                                                        and requested.action = 'open_item_adjustment_requested'
                                                                      and exists (
                                                                        select 1
                                                                        from audit_logs submitted
                                                                        where submitted.company_id = @company_id
                                                                          and submitted.entity_type = 'open_item_adjustment_request'
                                                                          and submitted.entity_id = @source_id
                                                                          and submitted.action = 'open_item_adjustment_request_submitted'
                                                                      )
                                                                      and not exists (
                                                                        select 1
                                                                        from audit_logs completed
                                                                        where completed.company_id = @company_id
                                                                          and completed.entity_type = 'open_item_adjustment_request'
                                                                          and completed.entity_id = @source_id
                                                                          and completed.action = 'open_item_adjustment_execution_completed'
                                                                      );
                                                                      """,
            _ => throw new NotSupportedException(
                $"Source type '{sourceType}' is not supported for source status updates.")
        };

        await using var command = scope.CreateCommand(sql);

        command.Parameters.AddWithValue("posted_at", postedAt);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("claim_id", Guid.NewGuid());

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        return affectedRows == 1;
    }
}
