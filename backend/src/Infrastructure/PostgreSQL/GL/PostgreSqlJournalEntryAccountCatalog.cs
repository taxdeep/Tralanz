using Modules.GL.JournalEntry;

namespace Infrastructure.PostgreSQL.GL;

public sealed class PostgreSqlJournalEntryAccountCatalog : IJournalEntryAccountCatalog
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlJournalEntryAccountCatalog(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<IReadOnlyList<JournalEntryAccountOption>> ListManualPostingAccountsAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              code,
              name,
              root_type,
              detail_type,
              coalesce(currency_code, '') as currency_code,
              allow_manual_posting
            from accounts
            where company_id = @company_id
              and is_active = true
              and allow_manual_posting = true
            order by code asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var options = new List<JournalEntryAccountOption>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var detailType = reader.IsDBNull(reader.GetOrdinal("detail_type"))
                ? reader.GetString(reader.GetOrdinal("root_type"))
                : reader.GetString(reader.GetOrdinal("detail_type"));

            options.Add(new JournalEntryAccountOption
            {
                AccountId = reader.GetGuid(reader.GetOrdinal("id")),
                Code = reader.GetString(reader.GetOrdinal("code")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                TypeLabel = ToTitle(detailType),
                CurrencyCode = reader.GetString(reader.GetOrdinal("currency_code")),
                AllowManualPosting = reader.GetBoolean(reader.GetOrdinal("allow_manual_posting"))
            });
        }

        return options;
    }

    private static string ToTitle(string value)
    {
        var words = value
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant());

        return string.Join(' ', words);
    }
}
