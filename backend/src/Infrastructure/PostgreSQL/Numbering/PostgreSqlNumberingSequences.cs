using Npgsql;

namespace Infrastructure.PostgreSQL.Numbering;

internal static class PostgreSqlNumberingSequences
{
    public static async Task<string> PeekAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        string scopeKey,
        string prefix,
        short padding,
        long seedNumber,
        CancellationToken cancellationToken)
    {
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
        Guid companyId,
        string scopeKey,
        string prefix,
        short padding,
        long seedNumber,
        CancellationToken cancellationToken)
    {
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
            set prefix = @prefix,
                padding = @padding,
                next_number = greatest(next_number, @seed_number) + 1,
                updated_at = now()
            where company_id = @company_id
              and scope_key = @scope_key
            returning prefix, greatest(next_number - 1, @seed_number) as issued_number, padding;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("scope_key", scopeKey);
        command.Parameters.AddWithValue("prefix", prefix);
        command.Parameters.AddWithValue("padding", padding);
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
        Guid companyId,
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
            set prefix = @prefix,
                padding = @padding,
                next_number = greatest(next_number, @seed_number),
                updated_at = now()
            where company_id = @company_id
              and scope_key = @scope_key;
            """;
        alignCommand.Parameters.AddWithValue("company_id", companyId);
        alignCommand.Parameters.AddWithValue("scope_key", scopeKey);
        alignCommand.Parameters.AddWithValue("prefix", prefix);
        alignCommand.Parameters.AddWithValue("padding", padding);
        alignCommand.Parameters.AddWithValue("seed_number", seedNumber);
        await alignCommand.ExecuteNonQueryAsync(cancellationToken);

        await NormalizeEntityNumberSequenceAsync(
            connection,
            transaction,
            companyId,
            scopeKey,
            prefix,
            seedNumber,
            cancellationToken);
    }

    private static async Task NormalizeEntityNumberSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        string scopeKey,
        string prefix,
        long seedNumber,
        CancellationToken cancellationToken)
    {
        var year = TryParseEntityNumberYear(scopeKey, prefix);
        if (!year.HasValue)
        {
            return;
        }

        var yearFloor = year.Value * 100_000_000L;
        var yearCeiling = (year.Value + 1L) * 100_000_000L;
        const long suffixCeiling = 100_000_000L;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update company_numbering_sequences
            set next_number = case
                  when next_number >= @year_floor and next_number < @year_ceiling
                    then greatest(next_number - @year_floor, @seed_number)
                  when next_number >= @suffix_ceiling
                    then @seed_number
                  else next_number
                end,
                updated_at = now()
            where company_id = @company_id
              and scope_key = @scope_key
              and (
                (next_number >= @year_floor and next_number < @year_ceiling)
                or next_number >= @suffix_ceiling
              );
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("scope_key", scopeKey);
        command.Parameters.AddWithValue("year_floor", yearFloor);
        command.Parameters.AddWithValue("year_ceiling", yearCeiling);
        command.Parameters.AddWithValue("suffix_ceiling", suffixCeiling);
        command.Parameters.AddWithValue("seed_number", seedNumber);
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
