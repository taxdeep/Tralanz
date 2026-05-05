using Npgsql;

namespace Citus.Accounting.Infrastructure.Persistence;

internal static class PostgresSourceDocumentDraftNumbering
{
    public static async Task<string> ReserveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string scopeKey,
        string prefix,
        short padding,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        var entityYear = TryParseEntityNumberYear(scopeKey, prefix);
        if (entityYear.HasValue)
        {
            return await ReserveEntityNumberAsync(
                connection,
                transaction,
                entityYear.Value,
                prefix,
                padding,
                seedNumber,
                cancellationToken);
        }

        await EnsureSeededAsync(
            connection,
            transaction,
            companyId,
            scopeKey,
            prefix,
            padding,
            seedNumber,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update company_numbering_sequences
            set next_number = greatest(next_number, @seed_number) + 1,
                updated_at = now()
            where company_id = @company_id
              and scope_key = @scope_key
            returning prefix, greatest(next_number - 1, @seed_number) as issued_number, padding;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("scope_key", scopeKey);
        command.Parameters.AddWithValue("seed_number", seedNumber);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var storedPrefix = reader.GetString(reader.GetOrdinal("prefix"));
        var issuedNumber = reader.GetInt64(reader.GetOrdinal("issued_number"));
        var storedPadding = reader.GetInt16(reader.GetOrdinal("padding"));
        return $"{storedPrefix}{issuedNumber.ToString().PadLeft(storedPadding, '0')}";
    }

    public static async Task<long> FindEntitySeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int year,
        CancellationToken cancellationToken)
    {
        // The core aggregator covers every document table that's
        // existed since the migration baseline. New tables added via
        // PostgresV1WriteFlowSchemaBootstrap (sales_receipts, etc.)
        // get scanned through ScanOptionalTableMaxSeedAsync below so
        // a partially-migrated DB (e.g. an integration test that
        // hasn't run the bootstrap yet) doesn't blow up the whole
        // entity-number lookup.
        await using var coreCommand = connection.CreateCommand();
        coreCommand.Transaction = transaction;
        coreCommand.CommandText =
            $"""
            with all_entities as (
              select entity_number from manual_journal_documents
              union all
              select entity_number from journal_entries
              union all
              select entity_number from invoices
              union all
              select entity_number from bills
              union all
              select entity_number from credit_notes
              union all
              select entity_number from vendor_credits
              union all
              select entity_number from receive_payments
              union all
              select entity_number from pay_bills
              union all
              select entity_number from credit_applications
              union all
              select entity_number from vendor_credit_applications
              union all
              select entity_number from fx_revaluation_batches
            )
            select coalesce(
              max(
                case
                  when entity_number ~ '^EN{year}[0-9]+$'
                    then substring(entity_number from 7)::bigint
                  else null
                end
              ),
              0
            )
            from all_entities;
            """;

        var coreMax = Convert.ToInt64(await coreCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

        // Scan optional tables (added by the V1 write-flow bootstrap).
        // Each call is a no-op if the table doesn't exist yet, so the
        // function stays safe across staged deployments.
        // Already in the list — kept here intentionally so a future
        // doc-type addition is a one-line edit; the helper itself is
        // tolerant of tables that don't exist yet via to_regclass.
        var optionalTables = new[]
        {
            "sales_receipts",
            "refund_receipts",
            "bank_transfers",
            "bank_deposits",
            "tax_returns",
        };

        var optionalMax = 0L;
        foreach (var table in optionalTables)
        {
            var tableMax = await ScanOptionalTableMaxSeedAsync(
                connection, transaction, table, year, cancellationToken);
            if (tableMax > optionalMax)
            {
                optionalMax = tableMax;
            }
        }

        return Math.Max(coreMax, optionalMax) + 1;
    }

    /// <summary>
    /// Returns max(seed) for the given table for the given year, or 0
    /// if the table doesn't exist yet. Wrapping the SELECT in a
    /// to_regclass check via PL/pgSQL keeps a partially-migrated DB
    /// from breaking entity-number reservation across the whole app.
    /// </summary>
    private static async Task<long> ScanOptionalTableMaxSeedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        int year,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            do $$
            declare
              max_seed bigint := 0;
            begin
              if to_regclass('public.{tableName}') is not null then
                execute format(
                  'select coalesce(max(case when entity_number ~ %L then substring(entity_number from 7)::bigint else null end), 0) from %I',
                  '^EN{year}[0-9]+$',
                  '{tableName}'
                ) into max_seed;
              end if;
              perform set_config('citus.optional_seed_max', max_seed::text, true);
            end $$;
            select coalesce(nullif(current_setting('citus.optional_seed_max', true), ''), '0')::bigint;
            """;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? 0L : Convert.ToInt64(result);
    }

    public static async Task<long> FindDisplaySeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string tableName,
        string displayColumn,
        string prefixPattern,
        int prefixLength,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select coalesce(
              max(
                case
                  when {displayColumn} ~ @prefix_pattern
                    then substring({displayColumn} from @prefix_length)::bigint
                  else null
                end
              ),
              0
            ) + 1
            from {tableName}
            where company_id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("prefix_pattern", prefixPattern);
        command.Parameters.AddWithValue("prefix_length", prefixLength);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
    }

    private static async Task EnsureSeededAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string scopeKey,
        string prefix,
        short padding,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        await using (var seedCommand = connection.CreateCommand())
        {
            seedCommand.Transaction = transaction;
            seedCommand.CommandText =
                """
                insert into company_numbering_sequences (
                  company_id,
                  scope_key,
                  prefix,
                  next_number,
                  padding,
                  suggestion_enabled,
                  updated_at
                )
                values (
                  @company_id,
                  @scope_key,
                  @prefix,
                  @next_number,
                  @padding,
                  true,
                  now()
                )
                on conflict (company_id, scope_key) do nothing;
                """;
            seedCommand.Parameters.AddWithValue("company_id", companyId);
            seedCommand.Parameters.AddWithValue("scope_key", scopeKey);
            seedCommand.Parameters.AddWithValue("prefix", prefix);
            seedCommand.Parameters.AddWithValue("next_number", seedNumber);
            seedCommand.Parameters.AddWithValue("padding", padding);
            await seedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var alignCommand = connection.CreateCommand();
        alignCommand.Transaction = transaction;
        alignCommand.CommandText =
            """
            update company_numbering_sequences
            set next_number = greatest(next_number, @seed_number),
                updated_at = now()
            where company_id = @company_id
              and scope_key = @scope_key;
            """;
        alignCommand.Parameters.AddWithValue("company_id", companyId);
        alignCommand.Parameters.AddWithValue("scope_key", scopeKey);
        alignCommand.Parameters.AddWithValue("seed_number", seedNumber);
        await alignCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int year,
        string prefix,
        short padding,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformEntityNumberSequenceAsync(connection, transaction, cancellationToken);

        await using (var seedCommand = connection.CreateCommand())
        {
            seedCommand.Transaction = transaction;
            seedCommand.CommandText =
                """
                insert into platform_entity_number_sequences (entity_year, next_number)
                values (@entity_year, @seed_number)
                on conflict (entity_year) do update
                  set next_number = greatest(platform_entity_number_sequences.next_number, excluded.next_number);
                """;
            seedCommand.Parameters.AddWithValue("entity_year", year);
            seedCommand.Parameters.AddWithValue("seed_number", seedNumber);
            await seedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update platform_entity_number_sequences
            set next_number = greatest(next_number, @seed_number) + 1
            where entity_year = @entity_year
            returning next_number - 1 as issued_number;
            """;
        command.Parameters.AddWithValue("entity_year", year);
        command.Parameters.AddWithValue("seed_number", seedNumber);

        var issuedNumber = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? seedNumber);
        return $"{prefix}{issuedNumber.ToString().PadLeft(padding, '0')}";
    }

    private static async Task EnsurePlatformEntityNumberSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            create table if not exists platform_entity_number_sequences (
              entity_year integer primary key,
              next_number bigint not null
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static int? TryParseEntityNumberYear(string scopeKey, string prefix)
    {
        if (!scopeKey.StartsWith("entity-number:", StringComparison.Ordinal) ||
            prefix.Length != 6 ||
            !prefix.StartsWith("EN", StringComparison.Ordinal) ||
            !int.TryParse(prefix.AsSpan(2), out var year))
        {
            return null;
        }

        return year;
    }
}
