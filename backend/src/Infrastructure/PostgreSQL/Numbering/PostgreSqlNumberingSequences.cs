using Npgsql;

namespace Infrastructure.PostgreSQL.Numbering;

internal static class PostgreSqlNumberingSequences
{
    public static async Task<string> PeekAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
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
            return await PeekEntityNumberAsync(
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
            select prefix, next_number, padding
            from company_numbering_sequences
            where company_id = @company_id
              and scope_key = @scope_key
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("scope_key", scopeKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var storedPrefix = reader.GetString(reader.GetOrdinal("prefix"));
        var nextNumber = reader.GetInt64(reader.GetOrdinal("next_number"));
        var storedPadding = reader.GetInt16(reader.GetOrdinal("padding"));
        return $"{storedPrefix}{nextNumber.ToString().PadLeft(storedPadding, '0')}";
    }

    public static async Task<string> ReserveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
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

    private static async Task EnsureSeededAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
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
        NpgsqlTransaction? transaction,
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
                "Company numbering schema has not been installed. Apply database migrations before reserving display numbers.");
        }
    }

    // Per-company entity-number peek/reserve. Both target the
    // company_entity_number_sequences (company_id, entity_year) row that
    // PostgresNumberingSequences and PostgresSourceDocumentDraftNumbering
    // also advance, so every audit-numbered writer in the platform shares
    // the same per-company counter.
    private static async Task<string> PeekEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        int year,
        string prefix,
        short padding,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        await EnsureEntityNumberSequenceInstalledAsync(connection, transaction, cancellationToken);
        await EnsureEntityNumberSeededAsync(connection, transaction, companyId, year, seedNumber, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select greatest(next_ordinal, @seed_number)
            from company_entity_number_sequences
            where company_id = @company_id and entity_year = @entity_year
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_year", year);
        command.Parameters.AddWithValue("seed_number", seedNumber);

        var nextNumber = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? seedNumber);
        return $"{prefix}{Base36.Encode(nextNumber, padding)}";
    }

    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        int year,
        string prefix,
        short padding,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        await EnsureEntityNumberSequenceInstalledAsync(connection, transaction, cancellationToken);
        await EnsureEntityNumberSeededAsync(connection, transaction, companyId, year, seedNumber, cancellationToken);

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
        return $"{prefix}{Base36.Encode(issuedNumber, padding)}";
    }

    private static async Task EnsureEntityNumberSequenceInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.Transaction = transaction;
        schemaCommand.CommandText =
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
        if (await schemaCommand.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException(
                "Entity number sequence schema has not been installed. Apply database migrations before reserving entity numbers.");
        }
    }

    private static async Task EnsureEntityNumberSeededAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        int year,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        await using var seedCommand = connection.CreateCommand();
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
