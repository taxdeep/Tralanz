using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;
using Npgsql;

namespace Infrastructure.PostgreSQL.UnitySearch;

public sealed class PostgreSqlUnitySearchQueryService(PostgreSqlConnectionFactory connections) : IUnitySearchQueryService
{
    public async Task<IReadOnlyList<SearchDocumentRecord>> SearchDocumentsAsync(
        UnitySearchQuery query,
        SearchPolicyDefinition policy,
        string normalizedQuery,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(query.Take, 1, 50);
        var results = new List<SearchDocumentRecord>();

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with ranked_results as (
              select
                doc.company_id,
                doc.entity_type,
                doc.source_id,
                doc.group_key,
                doc.primary_text,
                doc.secondary_text,
                doc.search_text,
                doc.exact_code_norm,
                doc.navigation_href,
                doc.metadata_json::text as metadata_json,
                doc.effective_date,
                doc.amount,
                doc.is_active,
                doc.is_voided,
                doc.rank_boost,
                doc.version,
                (
                  case
                    when doc.exact_code_norm = @normalized_query then 140
                    when doc.exact_code_norm like @exact_prefix then 90
                    else 0
                  end +
                  case
                    when lower(doc.primary_text) = @normalized_query then 80
                    when lower(doc.primary_text) like @text_prefix then 40
                    when lower(doc.primary_text) like @text_contains then 28
                    else 0
                  end +
                  case
                    when lower(doc.secondary_text) like @text_contains then 10
                    else 0
                  end +
                  case
                    when lower(doc.search_text) like @text_contains then 12
                    else 0
                  end +
                  doc.rank_boost +
                  coalesce(ln(1 + stats.click_count::numeric) * 5, 0) +
                  case
                    when stats.last_clicked_at_utc >= now() - interval '7 day' then 5
                    else 0
                  end
                ) as computed_score
              from search_documents doc
              left join search_click_stats stats
                on stats.company_id = doc.company_id
               and stats.user_id = @user_id
               and stats.context = @context
               and stats.entity_type = doc.entity_type
               and stats.source_id = doc.source_id
              where doc.company_id = @company_id
                and doc.entity_type = any(@entity_types)
                and (not @enforce_active_only or doc.is_active)
                and (not @enforce_business_eligibility or (doc.is_active and not doc.is_voided))
                and (
                  doc.exact_code_norm = @normalized_query
                  or doc.exact_code_norm like @exact_prefix
                  or lower(doc.primary_text) like @text_contains
                  or lower(doc.secondary_text) like @text_contains
                  or lower(doc.search_text) like @text_contains
                )
            )
            select
              company_id,
              entity_type,
              source_id,
              group_key,
              primary_text,
              secondary_text,
              search_text,
              exact_code_norm,
              navigation_href,
              metadata_json,
              effective_date,
              amount,
              is_active,
              is_voided,
              rank_boost,
              version,
              computed_score
            from ranked_results
            order by
              computed_score desc,
              effective_date desc nulls last,
              primary_text asc
            limit @take;
            """;
        command.Parameters.AddWithValue("company_id", query.CompanyId);
        command.Parameters.AddWithValue("user_id", query.UserId ?? Guid.Empty);
        command.Parameters.AddWithValue("context", query.Context);
        command.Parameters.AddWithValue("entity_types", policy.EntityTypes.ToArray());
        command.Parameters.AddWithValue("enforce_active_only", policy.EnforceActiveOnly);
        command.Parameters.AddWithValue("enforce_business_eligibility", policy.EnforceBusinessEligibility);
        command.Parameters.AddWithValue("normalized_query", normalizedQuery);
        command.Parameters.AddWithValue("exact_prefix", $"{normalizedQuery}%");
        command.Parameters.AddWithValue("text_prefix", $"{normalizedQuery}%");
        command.Parameters.AddWithValue("text_contains", $"%{normalizedQuery}%");
        command.Parameters.AddWithValue("take", take);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SearchDocumentRecord(
                reader.GetGuid(reader.GetOrdinal("company_id")),
                reader.GetString(reader.GetOrdinal("entity_type")),
                reader.GetGuid(reader.GetOrdinal("source_id")),
                reader.GetString(reader.GetOrdinal("group_key")),
                reader.GetString(reader.GetOrdinal("primary_text")),
                reader.GetString(reader.GetOrdinal("secondary_text")),
                reader.GetString(reader.GetOrdinal("search_text")),
                reader.GetString(reader.GetOrdinal("exact_code_norm")),
                reader.GetString(reader.GetOrdinal("navigation_href")),
                reader.GetString(reader.GetOrdinal("metadata_json")),
                reader.IsDBNull(reader.GetOrdinal("effective_date"))
                    ? null
                    : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("effective_date")),
                reader.IsDBNull(reader.GetOrdinal("amount"))
                    ? null
                    : reader.GetDecimal(reader.GetOrdinal("amount")),
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetBoolean(reader.GetOrdinal("is_voided")),
                reader.GetDecimal(reader.GetOrdinal("rank_boost")),
                reader.GetInt64(reader.GetOrdinal("version")),
                reader.GetDecimal(reader.GetOrdinal("computed_score"))));
        }

        return results;
    }
}
