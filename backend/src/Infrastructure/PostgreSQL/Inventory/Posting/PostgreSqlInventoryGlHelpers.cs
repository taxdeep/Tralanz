using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Inventory.Posting;

/// <summary>
/// Shared SQL plumbing for the three inventory GL posters
/// (Adjustment / Manufacturing / Transfer). After P0-3b-1 → P0-3b-3
/// shipped, the three posters carried ~600 LoC of identical helpers
/// (idempotency probe, account resolve, posting-period gate,
/// entity / display-number reservation, JE / line / ledger inserts).
/// This static class centralises those helpers so each poster reduces
/// to its leg-specific bits: source-type / posting-role labels,
/// Dr/Cr direction logic, idempotency-key shape, line descriptions.
///
/// All helpers operate on the caller's open NpgsqlConnection +
/// NpgsqlTransaction so the same-tx atomicity guarantee from the
/// originating PRs is preserved. None of the helpers commit or roll
/// back — that stays with the caller (which is the inventory store
/// that owns the outer transaction).
///
/// internal: callers live in the same assembly
/// (Infrastructure.PostgreSQL). Exposing publicly would invite cross-
/// project reuse of pieces that are tightly coupled to the
/// journal_entries / journal_entry_lines / ledger_entries schema.
/// </summary>
internal static class PostgreSqlInventoryGlHelpers
{
    private const short DisplayNumberPadding = 6;
    private const short EntityNumberPadding = 5;

    public static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    // ---------------------------------------------------------------------
    // Idempotency probe — (company_id, source_type, source_id) keyed.
    // ---------------------------------------------------------------------

    public static async Task<(Guid Id, string DisplayNumber)?> TryFindExistingByIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, display_number
            from journal_entries
            where company_id = @company_id
              and source_type = @source_type
              and source_id = @source_id
              and status = 'posted'
            order by created_at desc
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("display_number")));
    }

    // ---------------------------------------------------------------------
    // Account resolution — system_role primary, system_key fallback.
    // Mirror of PostgresAccountLookup (in the accounting infra project);
    // duplicated here because Infrastructure.PostgreSQL doesn't reference
    // the accounting infra layer.
    // ---------------------------------------------------------------------

    public static async Task<Guid?> ResolveAccountIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken,
        params string[] markers)
    {
        var normalized = markers
            .Where(static m => !string.IsNullOrWhiteSpace(m))
            .Select(static m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id
            from accounts
            where company_id = @company_id
              and is_active = true
              and (system_role = any(@markers) or system_key = any(@markers))
            order by
              case
                when system_role = any(@markers) then 0
                when system_key = any(@markers) then 1
                else 2
              end,
              code
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("markers", NpgsqlDbType.Array | NpgsqlDbType.Text, normalized);

        var resolved = await command.ExecuteScalarAsync(cancellationToken);
        return resolved is Guid id ? id : null;
    }

    // ---------------------------------------------------------------------
    // Posting-period gate — refuses to write into a period that's been
    // closed via company_book_governance_signals. Skipped when the
    // governance tables don't exist (test scaffolds, fresh deploys).
    // ---------------------------------------------------------------------

    public static async Task EnsurePostingPeriodOpenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        DateOnly postingDate,
        CancellationToken cancellationToken)
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText =
                """
                select to_regclass('public.company_books') is not null
                   and to_regclass('public.company_book_governance_signals') is not null;
                """;
            if (Convert.ToBoolean(await probe.ExecuteScalarAsync(cancellationToken) ?? false) is false)
            {
                return;
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select s.signal_date, s.reference_label
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
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("posting_date", postingDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var closedThrough = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("signal_date"));
        var label = reader.IsDBNull(reader.GetOrdinal("reference_label"))
            ? "closed period"
            : reader.GetString(reader.GetOrdinal("reference_label"));
        throw new InvalidOperationException(
            $"Posting date {postingDate:yyyy-MM-dd} is locked by {label} through {closedThrough:yyyy-MM-dd}.");
    }

    // ---------------------------------------------------------------------
    // Display number reservation: company_numbering_sequences scoped on
    // "journal-entry-display", prefix "JE-", padding 6. Identical to
    // PostgresNumberingSequences for JE display numbers.
    // ---------------------------------------------------------------------

    public static async Task<string> ReserveDisplayNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        const string scopeKey = "journal-entry-display";
        const string prefix = "JE-";

        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText =
                """
                insert into company_numbering_sequences (
                  company_id, scope_key, prefix, next_number, padding, suggestion_enabled, updated_at
                )
                values (@company_id, @scope_key, @prefix, 1, @padding, true, now())
                on conflict (company_id, scope_key) do nothing;
                """;
            seed.Parameters.AddWithValue("company_id", companyId.Value);
            seed.Parameters.AddWithValue("scope_key", scopeKey);
            seed.Parameters.AddWithValue("prefix", prefix);
            seed.Parameters.AddWithValue("padding", DisplayNumberPadding);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var advance = connection.CreateCommand();
        advance.Transaction = transaction;
        advance.CommandText =
            """
            update company_numbering_sequences
            set next_number = next_number + 1,
                updated_at = now()
            where company_id = @company_id and scope_key = @scope_key
            returning prefix, next_number - 1 as issued_number, padding;
            """;
        advance.Parameters.AddWithValue("company_id", companyId.Value);
        advance.Parameters.AddWithValue("scope_key", scopeKey);

        await using var reader = await advance.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"No numbering sequence row was returned for scope '{scopeKey}'.");
        }

        var issuedPrefix = reader.GetString(reader.GetOrdinal("prefix"));
        var issuedNumber = reader.GetInt64(reader.GetOrdinal("issued_number"));
        var issuedPadding = reader.GetInt16(reader.GetOrdinal("padding"));
        return $"{issuedPrefix}{issuedNumber.ToString().PadLeft(issuedPadding, '0')}";
    }

    // ---------------------------------------------------------------------
    // Entity number reservation: "EN{year}{base36-5}" format, scoped
    // on (company_id, entity_year). Mirrors
    // PostgresNumberingSequences.ReserveEntityNumberAsync.
    // ---------------------------------------------------------------------

    public static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        var seed = await FindEntitySeedNumberAsync(connection, transaction, companyId, year, cancellationToken);

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                insert into company_entity_number_sequences (company_id, entity_year, next_ordinal)
                values (@company_id, @entity_year, @seed_number)
                on conflict (company_id, entity_year) do update
                  set next_ordinal = greatest(company_entity_number_sequences.next_ordinal, excluded.next_ordinal);
                """;
            upsert.Parameters.AddWithValue("company_id", companyId.Value);
            upsert.Parameters.AddWithValue("entity_year", year);
            upsert.Parameters.AddWithValue("seed_number", seed);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var advance = connection.CreateCommand();
        advance.Transaction = transaction;
        advance.CommandText =
            """
            update company_entity_number_sequences
            set next_ordinal = greatest(next_ordinal, @seed_number) + 1
            where company_id = @company_id and entity_year = @entity_year
            returning next_ordinal - 1 as issued_number;
            """;
        advance.Parameters.AddWithValue("company_id", companyId.Value);
        advance.Parameters.AddWithValue("entity_year", year);
        advance.Parameters.AddWithValue("seed_number", seed);

        var issued = Convert.ToInt64(await advance.ExecuteScalarAsync(cancellationToken) ?? seed);
        return $"EN{year}{EncodeBase36(issued, EntityNumberPadding)}";
    }

    private static async Task<long> FindEntitySeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $$"""
            with all_entities as (
              select entity_number from manual_journal_documents where company_id = @company_id
              union all select entity_number from journal_entries where company_id = @company_id
              union all select entity_number from invoices where company_id = @company_id
              union all select entity_number from bills where company_id = @company_id
              union all select entity_number from credit_notes where company_id = @company_id
              union all select entity_number from vendor_credits where company_id = @company_id
              union all select entity_number from receive_payments where company_id = @company_id
              union all select entity_number from pay_bills where company_id = @company_id
              union all select entity_number from credit_applications where company_id = @company_id
              union all select entity_number from vendor_credit_applications where company_id = @company_id
              union all select entity_number from fx_revaluation_batches where company_id = @company_id
              union all select entity_number from accounts where company_id = @company_id
            )
            select coalesce(
              max(case when entity_number ~ '^EN{{year}}[0-9]+$'
                       then substring(entity_number from 7)::bigint
                       else null end),
              0
            ) + 1
            from all_entities;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
    }

    private static string EncodeBase36(long value, short padding)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0)
        {
            return new string('0', padding);
        }
        var stack = new Stack<char>();
        while (value > 0)
        {
            stack.Push(alphabet[(int)(value % 36)]);
            value /= 36;
        }
        var s = new string(stack.ToArray());
        return s.PadLeft(padding, '0');
    }

    // ---------------------------------------------------------------------
    // JE header insert. Column shapes mirror PostgresJournalEntryWriter
    // so the poster's rows look indistinguishable from journal-entry-
    // writer output (audit / reporting code doesn't need to know who
    // wrote it). exchange_rate is fixed at 1 and tx_* equals base_*
    // because every inventory GL leg posts in base currency.
    // ---------------------------------------------------------------------

    public static async Task InsertJournalEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalEntryId,
        CompanyId companyId,
        string entityNumber,
        string displayNumber,
        string sourceType,
        Guid sourceId,
        string baseCurrencyCode,
        DateOnly postingDate,
        string exchangeRateSource,
        decimal amount,
        string idempotencyKey,
        DateTimeOffset postedAt,
        UserId createdByUserId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into journal_entries (
              id, company_id, entity_number, display_number, status,
              source_type, source_id,
              transaction_currency_code, base_currency_code,
              exchange_rate, exchange_rate_date, exchange_rate_source,
              fx_rate_snapshot_id,
              total_tx_debit, total_tx_credit, total_debit, total_credit,
              posting_run_id, idempotency_key, posted_at, created_by_user_id
            )
            values (
              @id, @company_id, @entity_number, @display_number, 'posted',
              @source_type, @source_id,
              @transaction_currency_code, @base_currency_code,
              1, @posting_date, @exchange_rate_source,
              null,
              @total, @total, @total, @total,
              @posting_run_id, @idempotency_key, @posted_at, @created_by_user_id
            );
            """;
        command.Parameters.AddWithValue("id", journalEntryId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("display_number", displayNumber);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("transaction_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("posting_date", postingDate);
        command.Parameters.AddWithValue("exchange_rate_source", exchangeRateSource);
        command.Parameters.AddWithValue("total", amount);
        command.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("posted_at", postedAt);
        command.Parameters.AddWithValue("created_by_user_id", createdByUserId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------
    // JE line insert. posting_role is the only field that varies per
    // poster ("inventory_adjustment" / "inventory_manufacturing" /
    // "inventory_transfer"); everything else is identical SQL.
    // ---------------------------------------------------------------------

    public static async Task InsertJournalEntryLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid lineId,
        Guid journalEntryId,
        CompanyId companyId,
        int lineNumber,
        Guid accountId,
        string description,
        decimal debit,
        decimal credit,
        string postingRole,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into journal_entry_lines (
              id, company_id, journal_entry_id, line_number, account_id,
              description, party_type, party_id,
              tx_debit, tx_credit, debit, credit,
              tax_component_type, control_role, posting_role, source_line_number
            )
            values (
              @id, @company_id, @journal_entry_id, @line_number, @account_id,
              @description, null, null,
              @debit, @credit, @debit, @credit,
              null, null, @posting_role, null
            );
            """;
        command.Parameters.AddWithValue("id", lineId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        command.Parameters.AddWithValue("line_number", lineNumber);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("description", description);
        command.Parameters.AddWithValue("debit", debit);
        command.Parameters.AddWithValue("credit", credit);
        command.Parameters.AddWithValue("posting_role", postingRole);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------
    // Ledger entry insert. Identical across all three posters — no
    // per-poster parameters.
    // ---------------------------------------------------------------------

    public static async Task InsertLedgerEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ledgerId,
        Guid journalEntryId,
        Guid journalEntryLineId,
        CompanyId companyId,
        DateOnly postingDate,
        Guid accountId,
        decimal debit,
        decimal credit,
        string transactionCurrencyCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into ledger_entries (
              id, company_id, journal_entry_id, journal_entry_line_id,
              posting_date, account_id,
              debit, credit,
              transaction_currency_code, tx_debit, tx_credit
            )
            values (
              @id, @company_id, @journal_entry_id, @journal_entry_line_id,
              @posting_date, @account_id,
              @debit, @credit,
              @transaction_currency_code, @debit, @credit
            );
            """;
        command.Parameters.AddWithValue("id", ledgerId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        command.Parameters.AddWithValue("journal_entry_line_id", journalEntryLineId);
        command.Parameters.AddWithValue("posting_date", postingDate);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("debit", debit);
        command.Parameters.AddWithValue("credit", credit);
        command.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
