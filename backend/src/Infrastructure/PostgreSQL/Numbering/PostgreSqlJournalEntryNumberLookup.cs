using Engines.Numbering.JournalEntry;

namespace Infrastructure.PostgreSQL.Numbering;

public sealed class PostgreSqlJournalEntryNumberLookup : IJournalEntryNumberLookup
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlJournalEntryNumberLookup(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<string> GetNextDisplayNumberAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        var seedNumber = await FindSeedNumberAsync(connection, companyId, cancellationToken);
        return await PostgreSqlNumberingSequences.PeekAsync(
            connection,
            null,
            companyId,
            "journal-entry-display",
            "JE-",
            6,
            seedNumber,
            cancellationToken);
    }

    public async Task<string> ReserveNextDisplayNumberAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        var seedNumber = await FindSeedNumberAsync(connection, companyId, cancellationToken);
        return await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            null,
            companyId,
            "journal-entry-display",
            "JE-",
            6,
            seedNumber,
            cancellationToken);
    }

    private static async Task<long> FindSeedNumberAsync(
        Npgsql.NpgsqlConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select coalesce(
              max(
                case
                  when display_number ~ '^JE-[0-9]+$'
                    then substring(display_number from 4)::bigint
                  else null
                end
              ),
              0
            ) + 1
            from journal_entries
            where company_id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value ?? 1L);
    }
}
