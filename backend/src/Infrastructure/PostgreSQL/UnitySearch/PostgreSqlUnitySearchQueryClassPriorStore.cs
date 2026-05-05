using Citus.Modules.UnitySearch.Application.Contracts;
using Npgsql;

namespace Infrastructure.PostgreSQL.UnitySearch;

/// <summary>
/// Postgres-backed implementation of <see cref="IUnitySearchQueryClassPriorStore"/>.
/// Schema lives next to <c>search_documents</c> (created by
/// <c>PostgreSqlUnitySearchProjectionStore.EnsureSchemaAsync</c>) so a single
/// migration owns the search-side projection.
/// </summary>
public sealed class PostgreSqlUnitySearchQueryClassPriorStore(PostgreSqlConnectionFactory connections)
    : IUnitySearchQueryClassPriorStore
{
    public async Task RecordSelectAsync(
        CompanyId companyId,
        UserId userId,
        string queryClassTag,
        string entityType,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null
            || userId.Value is null
            || string.IsNullOrWhiteSpace(queryClassTag)
            || string.IsNullOrWhiteSpace(entityType))
        {
            return;
        }

        // Skip the learning write for query classes that don't help the
        // ranker. "empty" / "text" priors would just collect noise — the
        // existing rank_boost already orders text matches sensibly.
        if (queryClassTag is "empty" or "text")
        {
            return;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into search_query_class_priors (
              company_id,
              user_id,
              query_class,
              entity_type,
              click_count,
              last_clicked_at_utc
            )
            values (
              @company_id,
              @user_id,
              @query_class,
              @entity_type,
              1,
              now()
            )
            on conflict (company_id, user_id, query_class, entity_type)
            do update
            set click_count = search_query_class_priors.click_count + 1,
                last_clicked_at_utc = excluded.last_clicked_at_utc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("query_class", queryClassTag);
        command.Parameters.AddWithValue("entity_type", entityType);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
