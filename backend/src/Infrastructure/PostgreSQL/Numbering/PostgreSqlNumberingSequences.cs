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
        command.Parameters.AddWithValue("company_id", companyId);
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

    private static async Task<string> PeekEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int year,
        string prefix,
        short padding,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        await EnsureEntityNumberSeededAsync(connection, transaction, year, seedNumber, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select greatest(next_number, @seed_number)
            from platform_entity_number_sequences
            where entity_year = @entity_year
            limit 1;
            """;
        command.Parameters.AddWithValue("entity_year", year);
        command.Parameters.AddWithValue("seed_number", seedNumber);

        var nextNumber = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? seedNumber);
        return $"{prefix}{nextNumber.ToString().PadLeft(padding, '0')}";
    }

    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int year,
        string prefix,
        short padding,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        await EnsureEntityNumberSeededAsync(connection, transaction, year, seedNumber, cancellationToken);

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

    private static async Task EnsureEntityNumberSeededAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int year,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.Transaction = transaction;
            schemaCommand.CommandText =
                """
                create table if not exists platform_entity_number_sequences (
                  entity_year integer primary key,
                  next_number bigint not null
                );
                """;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var seedCommand = connection.CreateCommand();
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
