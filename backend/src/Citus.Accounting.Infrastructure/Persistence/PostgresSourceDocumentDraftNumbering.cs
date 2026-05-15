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
                companyId,
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
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("scope_key", scopeKey);
        command.Parameters.AddWithValue("seed_number", seedNumber);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var storedPrefix = reader.GetString(reader.GetOrdinal("prefix"));
        var issuedNumber = reader.GetInt64(reader.GetOrdinal("issued_number"));
        var storedPadding = reader.GetInt16(reader.GetOrdinal("padding"));
        return $"{storedPrefix}{issuedNumber.ToString().PadLeft(storedPadding, '0')}";
    }

    // Per-company entity-number seed scanner. Filters every document
    // table by company_id so each company's seed reflects only its own
    // audit history; without that filter cross-company writes inflate
    // the seed and force later allocations to skip ordinals.
    public static async Task<long> FindEntitySeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        await using var coreCommand = connection.CreateCommand();
        coreCommand.Transaction = transaction;
        coreCommand.CommandText =
            $"""
            with all_entities as (
              select entity_number from manual_journal_documents where company_id = @company_id
              union all
              select entity_number from journal_entries where company_id = @company_id
              union all
              select entity_number from invoices where company_id = @company_id
              union all
              select entity_number from bills where company_id = @company_id
              union all
              select entity_number from credit_notes where company_id = @company_id
              union all
              select entity_number from vendor_credits where company_id = @company_id
              union all
              select entity_number from receive_payments where company_id = @company_id
              union all
              select entity_number from pay_bills where company_id = @company_id
              union all
              select entity_number from credit_applications where company_id = @company_id
              union all
              select entity_number from vendor_credit_applications where company_id = @company_id
              union all
              select entity_number from fx_revaluation_batches where company_id = @company_id
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
        coreCommand.Parameters.AddWithValue("company_id", companyId.Value);

        var coreMax = Convert.ToInt64(await coreCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

        // Scan optional tables (added by the V1 write-flow bootstrap).
        // Each call is a no-op if the table doesn't exist yet, so the
        // function stays safe across staged deployments.
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
                connection, transaction, companyId, table, year, cancellationToken);
            if (tableMax > optionalMax)
            {
                optionalMax = tableMax;
            }
        }

        return Math.Max(coreMax, optionalMax) + 1;
    }

    /// <summary>
    /// Returns max(seed) for the given table for the given (company, year),
    /// or 0 if the table doesn't exist yet. Wrapping the SELECT in a
    /// to_regclass check via PL/pgSQL keeps a partially-migrated DB
    /// from breaking entity-number reservation across the whole app.
    /// </summary>
    private static async Task<long> ScanOptionalTableMaxSeedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string tableName,
        int year,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Embed company_id literally because the dynamic execute() inside the
        // DO block runs in its own scope and won't see Npgsql parameters.
        // CompanyId is a 7-char fixed-shape string from a closed alphabet, so
        // there is no SQL-injection surface — but we still validate length
        // before formatting it in to keep the assumption explicit.
        var companyLiteral = companyId.Value;
        if (companyLiteral.Length != 7)
        {
            throw new InvalidOperationException($"Unexpected company_id width: '{companyLiteral}'.");
        }
        command.CommandText =
            $"""
            do $$
            declare
              max_seed bigint := 0;
            begin
              if to_regclass('public.{tableName}') is not null then
                execute format(
                  'select coalesce(max(case when entity_number ~ %L then substring(entity_number from 7)::bigint else null end), 0) from %I where company_id = %L',
                  '^EN{year}[0-9]+$',
                  '{tableName}',
                  '{companyLiteral}'
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
        command.Parameters.AddWithValue("company_id", companyId.Value);
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
        await EnsureCompanyNumberingSequenceInstalledAsync(connection, transaction, cancellationToken);

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
            seedCommand.Parameters.AddWithValue("company_id", companyId.Value);
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
        alignCommand.Parameters.AddWithValue("company_id", companyId.Value);
        alignCommand.Parameters.AddWithValue("scope_key", scopeKey);
        alignCommand.Parameters.AddWithValue("seed_number", seedNumber);
        await alignCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureCompanyNumberingSequenceInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              to_regclass('company_numbering_sequences') is not null
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'company_numbering_sequences'
                  and column_name = 'company_id')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'company_numbering_sequences'
                  and column_name = 'scope_key')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'company_numbering_sequences'
                  and column_name = 'next_number')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'company_numbering_sequences'
                  and column_name = 'suggestion_enabled');
            """;
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException(
                "Company numbering schema has not been installed. Apply database migrations before reserving document numbers.");
        }
    }

    // Per-company entity-number reservation. Targets the same
    // company_entity_number_sequences table that PostgresNumberingSequences
    // uses, so source-document draft writers (vendor credits, tax returns,
    // sales/refund receipts) advance the same per-company counter every
    // other AP/AR write does. The pre-2026-05-07 implementation wrote
    // platform_entity_number_sequences (year-only key), which is why two
    // companies could trip uq_*_company_entity_number when their seeds
    // happened to land on the same ordinal.
    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        string prefix,
        short padding,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        await EnsureCompanyEntityNumberSequenceInstalledAsync(connection, transaction, cancellationToken);

        await using (var seedCommand = connection.CreateCommand())
        {
            seedCommand.Transaction = transaction;
            seedCommand.CommandText =
                """
                insert into company_entity_number_sequences (company_id, entity_year, next_ordinal)
                values (@company_id, @entity_year, @seed_number)
                on conflict (company_id, entity_year) do update
                  set next_ordinal = greatest(company_entity_number_sequences.next_ordinal, excluded.next_ordinal);
                """;
            seedCommand.Parameters.AddWithValue("company_id", companyId.Value);
            seedCommand.Parameters.AddWithValue("entity_year", year);
            seedCommand.Parameters.AddWithValue("seed_number", seedNumber);
            await seedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update company_entity_number_sequences
            set next_ordinal = greatest(next_ordinal, @seed_number) + 1
            where company_id = @company_id and entity_year = @entity_year
            returning next_ordinal - 1 as issued_number;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_year", year);
        command.Parameters.AddWithValue("seed_number", seedNumber);

        var issuedNumber = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? seedNumber);
        // EntityNumber format: EN + YYYY + 5 base36 chars (11 chars total).
        // The `padding` parameter is the base36 width (typically 5).
        return $"{prefix}{Base36.Encode(issuedNumber, padding)}";
    }

    private static async Task EnsureCompanyEntityNumberSequenceInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              to_regclass('company_entity_number_sequences') is not null
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'company_entity_number_sequences'
                  and column_name = 'company_id')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'company_entity_number_sequences'
                  and column_name = 'entity_year')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'company_entity_number_sequences'
                  and column_name = 'next_ordinal');
            """;
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException(
                "Entity number sequence schema has not been installed. Apply database migrations before posting source documents.");
        }
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
