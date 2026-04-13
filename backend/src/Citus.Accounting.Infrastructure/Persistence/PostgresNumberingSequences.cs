namespace Citus.Accounting.Infrastructure.Persistence;

internal static class PostgresNumberingSequences
{
    public static async Task<string> ReserveAsync(
        PostgresCommandScope scope,
        Guid companyId,
        string scopeKey,
        string prefix,
        short padding,
        CancellationToken cancellationToken)
    {
        await using (var seedCommand = scope.CreateCommand(
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
                           1,
                           @padding,
                           true,
                           now()
                         )
                         on conflict (company_id, scope_key) do nothing;
                         """))
        {
            seedCommand.Parameters.AddWithValue("company_id", companyId);
            seedCommand.Parameters.AddWithValue("scope_key", scopeKey);
            seedCommand.Parameters.AddWithValue("prefix", prefix);
            seedCommand.Parameters.AddWithValue("padding", padding);
            await seedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = scope.CreateCommand(
            """
            update company_numbering_sequences
            set prefix = @prefix,
                padding = @padding,
                next_number = next_number + 1,
                updated_at = now()
            where company_id = @company_id
              and scope_key = @scope_key
            returning prefix, next_number - 1 as issued_number, padding;
            """);

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("scope_key", scopeKey);
        command.Parameters.AddWithValue("prefix", prefix);
        command.Parameters.AddWithValue("padding", padding);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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
}
