using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Inventory.Posting;

/// <summary>
/// PostgreSQL implementation of <see cref="IInventoryAdjustmentGlPoster"/>.
/// Writes the JE header + 2 lines + 2 ledger entries on the inventory
/// store's existing transaction so the subledger and GL commit (or roll
/// back) together.
///
/// Idempotency: a SELECT against journal_entries
/// (<c>company_id, source_type='inventory_adjustment_gl', source_id=docId</c>)
/// runs inside the same tx before any INSERT. If a row exists, the
/// poster returns its identifiers with AlreadyPosted=true and does
/// nothing else. The inventory_documents row's outer tx serializes
/// against a concurrent retry; combined with the inventory store's
/// idempotency_key partial unique index, the two paths protect against
/// duplicate posting.
///
/// Numbering: reuses the company_numbering_sequences + entity_number
/// scheme already used by PostgresJournalEntryWriter (display number
/// "JE-NNNNNN" zero-padded to 6, entity number "EN{year}{base36}"
/// zero-padded to 5). The two reservations run on the inventory
/// store's tx so concurrent posters serialize via the row's UPDATE
/// lock.
/// </summary>
public sealed class PostgreSqlInventoryAdjustmentGlPoster : IInventoryAdjustmentGlPoster
{
    private const string SourceType = "inventory_adjustment_gl";
    private const string InventoryAssetSystemRole = "inventory_asset";
    private const string InventoryAssetSystemKey = "inventory:asset";
    private const string InventoryAdjustmentSystemRole = "inventory_adjustment";
    private const string InventoryAdjustmentSystemKey = "inventory:adjustment";
    private const string ExchangeRateSource = "base_currency_inventory_adjustment";
    private const short DisplayNumberPadding = 6;
    private const short EntityNumberPadding = 5;

    public async Task<InventoryAdjustmentGlPostingResult> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InventoryAdjustmentGlPostingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);

        if (request.TotalCostBase <= 0m)
        {
            throw new InvalidOperationException(
                $"Inventory adjustment GL posting requires a strictly positive total cost; got {request.TotalCostBase}.");
        }

        if (string.IsNullOrWhiteSpace(request.BaseCurrencyCode))
        {
            throw new InvalidOperationException("Base currency code is required for inventory adjustment GL posting.");
        }

        // 1. Idempotency probe: a prior post returns its identifiers.
        //    The probe runs on the inventory store's tx so a concurrent
        //    retry can't sneak between probe and insert.
        var existing = await TryFindExistingByIdempotencyAsync(
            connection,
            transaction,
            request.CompanyId,
            request.InventoryDocumentId,
            cancellationToken);
        if (existing is not null)
        {
            return new InventoryAdjustmentGlPostingResult(
                existing.Value.Id,
                existing.Value.DisplayNumber,
                AlreadyPosted: true);
        }

        // 2. Resolve the two SystemRole-pinned accounts. Q2=A specifies
        //    Adjustment Gain → Dr inventory_asset / Cr inventory_adjustment,
        //    Adjustment Loss + Write-off → Dr inventory_adjustment / Cr inventory_asset.
        //    Both accounts are pinned in the starter COA (14000 + 64600),
        //    so resolution failure means the operator's COA is broken.
        var inventoryAssetAccountId = await ResolveAccountIdAsync(
            connection, transaction, request.CompanyId, cancellationToken,
            InventoryAssetSystemRole, InventoryAssetSystemKey);
        if (inventoryAssetAccountId is null)
        {
            throw new InvalidOperationException(
                "No active account is bound to system role 'inventory_asset' (starter COA: 14000 Inventory). " +
                "Pin the Inventory account's system role via the activation wizard before posting inventory adjustments.");
        }

        var inventoryAdjustmentAccountId = await ResolveAccountIdAsync(
            connection, transaction, request.CompanyId, cancellationToken,
            InventoryAdjustmentSystemRole, InventoryAdjustmentSystemKey);
        if (inventoryAdjustmentAccountId is null)
        {
            throw new InvalidOperationException(
                "No active account is bound to system role 'inventory_adjustment' (starter COA: 64600 Inventory Adjustment). " +
                "Pin the Inventory Adjustment account's system role via the activation wizard before posting inventory adjustments.");
        }

        // 3. Posting-period gate — refuse to write into a closed period.
        await EnsurePostingPeriodOpenAsync(
            connection, transaction, request.CompanyId, request.PostingDate, cancellationToken);

        // 4. Reserve entity_number + display_number on the inventory tx.
        var postingYear = request.PostingDate.Year;
        var entityNumber = await ReserveEntityNumberAsync(
            connection, transaction, request.CompanyId, postingYear, cancellationToken);
        var displayNumber = await ReserveDisplayNumberAsync(
            connection, transaction, request.CompanyId, cancellationToken);

        // 5. Compute Dr/Cr direction from the adjustment kind. The
        //    amount is the inventory store's totalCostBase, which is the
        //    sum of cost_delta_base across the just-written
        //    inventory_ledger_entries for this document.
        var amount = Round6(request.TotalCostBase);
        var (debitAccountId, creditAccountId) = request.Kind switch
        {
            InventoryAdjustmentGlKind.Gain => (inventoryAssetAccountId.Value, inventoryAdjustmentAccountId.Value),
            InventoryAdjustmentGlKind.Loss => (inventoryAdjustmentAccountId.Value, inventoryAssetAccountId.Value),
            InventoryAdjustmentGlKind.WriteOff => (inventoryAdjustmentAccountId.Value, inventoryAssetAccountId.Value),
            _ => throw new InvalidOperationException($"Unsupported inventory adjustment GL kind '{request.Kind}'.")
        };

        // 6. Insert journal entry header + 2 lines + 2 ledger entries.
        var journalEntryId = Guid.NewGuid();
        var postedAt = DateTimeOffset.UtcNow;
        var idempotencyKey = $"inventory-adjustment-gl:{request.CompanyId.Value}:{request.InventoryDocumentId}";
        var description = BuildLineDescription(request);

        await InsertJournalEntryAsync(
            connection, transaction,
            journalEntryId, request.CompanyId, entityNumber, displayNumber,
            request.InventoryDocumentId, request.BaseCurrencyCode,
            request.PostingDate, amount, idempotencyKey, postedAt, request.UserId,
            cancellationToken);

        var debitLineId = Guid.NewGuid();
        await InsertJournalEntryLineAsync(
            connection, transaction,
            debitLineId, journalEntryId, request.CompanyId,
            lineNumber: 1, accountId: debitAccountId, description: description,
            debit: amount, credit: 0m,
            cancellationToken);

        var creditLineId = Guid.NewGuid();
        await InsertJournalEntryLineAsync(
            connection, transaction,
            creditLineId, journalEntryId, request.CompanyId,
            lineNumber: 2, accountId: creditAccountId, description: description,
            debit: 0m, credit: amount,
            cancellationToken);

        await InsertLedgerEntryAsync(
            connection, transaction,
            ledgerId: Guid.NewGuid(), journalEntryId, debitLineId,
            request.CompanyId, request.PostingDate, debitAccountId,
            debit: amount, credit: 0m, request.BaseCurrencyCode,
            cancellationToken);

        await InsertLedgerEntryAsync(
            connection, transaction,
            ledgerId: Guid.NewGuid(), journalEntryId, creditLineId,
            request.CompanyId, request.PostingDate, creditAccountId,
            debit: 0m, credit: amount, request.BaseCurrencyCode,
            cancellationToken);

        return new InventoryAdjustmentGlPostingResult(
            journalEntryId,
            displayNumber,
            AlreadyPosted: false);
    }

    // ---------------------------------------------------------------------
    // Idempotency
    // ---------------------------------------------------------------------

    private static async Task<(Guid Id, string DisplayNumber)?> TryFindExistingByIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid inventoryDocumentId,
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
        command.Parameters.AddWithValue("source_type", SourceType);
        command.Parameters.AddWithValue("source_id", inventoryDocumentId);

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
    // Account resolution — mirrors PostgresAccountLookup (kept inlined so
    // Infrastructure.PostgreSQL doesn't take a reference on
    // Citus.Accounting.Infrastructure's internals).
    // ---------------------------------------------------------------------

    private static async Task<Guid?> ResolveAccountIdAsync(
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
    // Posting period gate — refuses to write into a period that's been
    // closed via company_book_governance_signals (mirror of
    // PostgresJournalEntryWriter.EnsurePostingPeriodOpenAsync).
    // ---------------------------------------------------------------------

    private static async Task EnsurePostingPeriodOpenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        DateOnly postingDate,
        CancellationToken cancellationToken)
    {
        // Some test scaffolds don't install the governance tables. If
        // the tables are absent, skip the gate — the same behavior the
        // accounting writer follows.
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
    // Numbering — inlined mirrors of PostgresNumberingSequences so
    // Infrastructure.PostgreSQL doesn't have to cross-project-reference
    // Citus.Accounting.Infrastructure's internals. Identical SQL shape.
    // ---------------------------------------------------------------------

    private static async Task<string> ReserveDisplayNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        const string scopeKey = "journal-entry-display";
        const string prefix = "JE-";

        // Seed the row on first reservation. ON CONFLICT DO NOTHING is
        // safe under concurrent inserts; whichever transaction wins
        // sets next_number=1 and the loser is a no-op.
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

    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        // Seed: pick the highest existing EN{year} ordinal across every
        // audit-numbered table for this company, +1. Mirrors
        // PostgresNumberingSequences.FindEntitySeedNumberAsync.
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
    // INSERTs — column shapes mirror PostgresJournalEntryWriter so the
    // poster's rows look indistinguishable from journal-entry-writer
    // output (audit / reporting code doesn't need to know who wrote it).
    // ---------------------------------------------------------------------

    private static async Task InsertJournalEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalEntryId,
        CompanyId companyId,
        string entityNumber,
        string displayNumber,
        Guid sourceId,
        string baseCurrencyCode,
        DateOnly postingDate,
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
        command.Parameters.AddWithValue("source_type", SourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("transaction_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("posting_date", postingDate);
        command.Parameters.AddWithValue("exchange_rate_source", ExchangeRateSource);
        command.Parameters.AddWithValue("total", amount);
        command.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("posted_at", postedAt);
        command.Parameters.AddWithValue("created_by_user_id", createdByUserId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertJournalEntryLineAsync(
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
        command.Parameters.AddWithValue("posting_role", "inventory_adjustment");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLedgerEntryAsync(
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

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static string BuildLineDescription(InventoryAdjustmentGlPostingRequest request)
    {
        var label = request.Kind switch
        {
            InventoryAdjustmentGlKind.Gain => "Inventory adjustment gain",
            InventoryAdjustmentGlKind.Loss => "Inventory adjustment loss",
            InventoryAdjustmentGlKind.WriteOff => "Inventory write-off",
            _ => "Inventory adjustment"
        };
        return $"{label} — {request.InventoryDocumentNumber}";
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
