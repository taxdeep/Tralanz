using Citus.Modules.UnitySearch.Application.Contracts;
using Npgsql;

namespace Infrastructure.PostgreSQL.UnitySearch;

public sealed class PostgreSqlUnitySearchStatsStore(PostgreSqlConnectionFactory connections) : IUnitySearchStatsStore
{
    private int _schemaEnsured;

    public async Task<IReadOnlyList<UnitySearchRecentQueryRecord>> ListRecentQueriesAsync(
        Guid companyId,
        Guid userId,
        string context,
        int take,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var items = new List<UnitySearchRecentQueryRecord>();

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select context, query_text, used_at_utc
            from search_recent_queries
            where company_id = @company_id
              and user_id = @user_id
              and context = @context
            order by used_at_utc desc
            limit @take;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("context", context);
        command.Parameters.AddWithValue("take", Math.Clamp(take, 1, 20));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new UnitySearchRecentQueryRecord
            {
                Context = reader.GetString(reader.GetOrdinal("context")),
                QueryText = reader.GetString(reader.GetOrdinal("query_text")),
                UsedAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("used_at_utc"))
            });
        }

        return items;
    }

    public async Task RecordQueryAsync(
        Guid companyId,
        Guid userId,
        string context,
        string queryText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    insert into search_recent_queries (
                      company_id,
                      user_id,
                      context,
                      query_text,
                      used_at_utc
                    )
                    values (
                      @company_id,
                      @user_id,
                      @context,
                      @query_text,
                      now()
                    )
                    on conflict (company_id, user_id, context, query_text)
                    do update
                    set used_at_utc = excluded.used_at_utc;
                    """;
                command.Parameters.AddWithValue("company_id", companyId);
                command.Parameters.AddWithValue("user_id", userId);
                command.Parameters.AddWithValue("context", context);
                command.Parameters.AddWithValue("query_text", queryText);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var trimCommand = connection.CreateCommand())
            {
                trimCommand.Transaction = transaction;
                trimCommand.CommandText =
                    """
                    delete from search_recent_queries
                    where company_id = @company_id
                      and user_id = @user_id
                      and context = @context
                      and query_text not in (
                        select query_text
                        from search_recent_queries
                        where company_id = @company_id
                          and user_id = @user_id
                          and context = @context
                        order by used_at_utc desc
                        limit 20
                      );
                    """;
                trimCommand.Parameters.AddWithValue("company_id", companyId);
                trimCommand.Parameters.AddWithValue("user_id", userId);
                trimCommand.Parameters.AddWithValue("context", context);
                await trimCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task RecordClickAsync(
        Guid companyId,
        Guid userId,
        string context,
        string entityType,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into search_click_stats (
              company_id,
              user_id,
              context,
              entity_type,
              source_id,
              click_count,
              last_clicked_at_utc
            )
            values (
              @company_id,
              @user_id,
              @context,
              @entity_type,
              @source_id,
              1,
              now()
            )
            on conflict (company_id, user_id, context, entity_type, source_id)
            do update
            set click_count = search_click_stats.click_count + 1,
                last_clicked_at_utc = excluded.last_clicked_at_utc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("context", context);
        command.Parameters.AddWithValue("entity_type", entityType);
        command.Parameters.AddWithValue("source_id", sourceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaEnsured) == 1)
        {
            return;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table if not exists search_recent_queries (
              company_id uuid not null,
              user_id uuid not null,
              context text not null,
              query_text text not null,
              used_at_utc timestamptz not null,
              primary key (company_id, user_id, context, query_text)
            );

            create index if not exists ix_search_recent_queries_lookup
              on search_recent_queries (company_id, user_id, context, used_at_utc desc);

            create table if not exists search_click_stats (
              company_id uuid not null,
              user_id uuid not null,
              context text not null,
              entity_type text not null,
              source_id uuid not null,
              click_count integer not null default 0,
              last_clicked_at_utc timestamptz not null,
              primary key (company_id, user_id, context, entity_type, source_id)
            );

            create index if not exists ix_search_click_stats_lookup
              on search_click_stats (company_id, user_id, context, last_clicked_at_utc desc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        Volatile.Write(ref _schemaEnsured, 1);
    }
}
