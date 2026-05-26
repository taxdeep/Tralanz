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
        UnitySearchQueryHints hints,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(query.Take, 1, 50);
        var results = new List<SearchDocumentRecord>();
        var hasText = !string.IsNullOrWhiteSpace(normalizedQuery);
        var hasNumeric = hints.IsNumeric;
        var numericValue = hints.NumericValue ?? 0m;
        var candidateLimit = Math.Clamp(take * 8, 40, 400);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with query_terms as (
              select websearch_to_tsquery('simple', @fts_query) as tsq
            ),
            candidate_keys as (
              select
                company_id,
                entity_type,
                source_id,
                max(path_score) as path_score
              from (
                (
                select
                  doc.company_id,
                  doc.entity_type,
                  doc.source_id,
                  160::numeric as path_score
                from search_documents doc
                where @has_text
                  and doc.company_id = @company_id
                  and doc.entity_type = any(@entity_types)
                  and not doc.is_voided
                  and (not @enforce_active_only or doc.is_active)
                  and (not @enforce_business_eligibility or (doc.is_active and not doc.is_voided))
                  and doc.exact_code_norm = @normalized_query
                limit @candidate_limit
                )

                union all

                (
                select
                  doc.company_id,
                  doc.entity_type,
                  doc.source_id,
                  105::numeric as path_score
                from search_documents doc
                where @has_text
                  and doc.company_id = @company_id
                  and doc.entity_type = any(@entity_types)
                  and not doc.is_voided
                  and (not @enforce_active_only or doc.is_active)
                  and (not @enforce_business_eligibility or (doc.is_active and not doc.is_voided))
                  and doc.exact_code_norm like @exact_prefix
                limit @candidate_limit
                )

                union all

                (
                select
                  doc.company_id,
                  doc.entity_type,
                  doc.source_id,
                  (70 + ts_rank_cd(doc.search_vector, query_terms.tsq) * 45)::numeric as path_score
                from search_documents doc
                cross join query_terms
                where @has_text
                  and query_terms.tsq::text <> ''
                  and doc.company_id = @company_id
                  and doc.entity_type = any(@entity_types)
                  and not doc.is_voided
                  and (not @enforce_active_only or doc.is_active)
                  and (not @enforce_business_eligibility or (doc.is_active and not doc.is_voided))
                  and doc.search_vector @@ query_terms.tsq
                order by ts_rank_cd(doc.search_vector, query_terms.tsq) desc, doc.rank_boost desc
                limit @candidate_limit
                )

                union all

                (
                select
                  doc.company_id,
                  doc.entity_type,
                  doc.source_id,
                  case
                    when lower(doc.primary_text) = @normalized_query then 95::numeric
                    when lower(doc.primary_text) like @text_prefix then 62::numeric
                    when lower(doc.primary_text) like @text_contains then 44::numeric
                    when lower(doc.secondary_text) like @text_contains then 24::numeric
                    else 18::numeric
                  end as path_score
                from search_documents doc
                where @has_text
                  and doc.company_id = @company_id
                  and doc.entity_type = any(@entity_types)
                  and not doc.is_voided
                  and (not @enforce_active_only or doc.is_active)
                  and (not @enforce_business_eligibility or (doc.is_active and not doc.is_voided))
                  and (
                    lower(doc.primary_text) like @text_contains
                    or lower(doc.secondary_text) like @text_contains
                    or lower(doc.search_text) like @text_contains
                  )
                order by
                  case
                    when lower(doc.primary_text) = @normalized_query then 0
                    when lower(doc.primary_text) like @text_prefix then 1
                    when lower(doc.primary_text) like @text_contains then 2
                    when lower(doc.secondary_text) like @text_contains then 3
                    else 4
                  end,
                  doc.rank_boost desc,
                  doc.primary_text asc
                limit @candidate_limit
                )

                union all

                (
                select
                  doc.company_id,
                  doc.entity_type,
                  doc.source_id,
                  case
                    when doc.amount = @numeric_query then 220::numeric
                    else 140::numeric
                  end as path_score
                from search_documents doc
                where @has_numeric
                  and doc.company_id = @company_id
                  and doc.entity_type = any(@entity_types)
                  and not doc.is_voided
                  and (not @enforce_active_only or doc.is_active)
                  and (not @enforce_business_eligibility or (doc.is_active and not doc.is_voided))
                  and doc.amount is not null
                  and (doc.amount = @numeric_query or abs(doc.amount - @numeric_query) < 0.005)
                limit @candidate_limit
                )
              ) candidates
              group by company_id, entity_type, source_id
            ),
            ranked_results as (
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
                  candidates.path_score +
                  case
                    when @has_numeric and doc.amount is not null and doc.amount = @numeric_query then 200
                    when @has_numeric and doc.amount is not null and abs(doc.amount - @numeric_query) < 0.005 then 120
                    else 0
                  end +
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
                  end +
                  coalesce(ln(1 + prior.click_count::numeric) * 8, 0) +
                  case
                    when prior.last_clicked_at_utc >= now() - interval '30 day' then 4
                    else 0
                  end +
                  case
                    when @query_class = 'numeric_decimal' and doc.entity_type = 'journal_entry' then 8
                    when @query_class = 'numeric_decimal' and doc.entity_type in ('invoice','bill') then 5
                    when @query_class = 'numeric_decimal' and doc.entity_type in ('credit_note','vendor_credit') then 3
                    else 0
                  end
                ) as computed_score
              from search_documents doc
              join candidate_keys candidates
                on candidates.company_id = doc.company_id
               and candidates.entity_type = doc.entity_type
               and candidates.source_id = doc.source_id
              left join search_click_stats stats
                on stats.company_id = doc.company_id
               and stats.user_id = @user_id
               and stats.context = @context
               and stats.entity_type = doc.entity_type
               and stats.source_id = doc.source_id
              left join search_query_class_priors prior
                on prior.company_id = doc.company_id
               and prior.user_id = @user_id
               and prior.query_class = @query_class
               and prior.entity_type = doc.entity_type
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
        command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
        command.Parameters.AddWithValue("user_id", query.UserId?.Value ?? string.Empty);
        command.Parameters.AddWithValue("context", query.Context);
        command.Parameters.AddWithValue("entity_types", policy.EntityTypes.ToArray());
        command.Parameters.AddWithValue("enforce_active_only", policy.EnforceActiveOnly);
        command.Parameters.AddWithValue("enforce_business_eligibility", policy.EnforceBusinessEligibility);
        command.Parameters.AddWithValue("normalized_query", normalizedQuery);
        command.Parameters.AddWithValue("fts_query", normalizedQuery);
        command.Parameters.AddWithValue("exact_prefix", $"{normalizedQuery}%");
        command.Parameters.AddWithValue("text_prefix", $"{normalizedQuery}%");
        command.Parameters.AddWithValue("text_contains", $"%{normalizedQuery}%");
        command.Parameters.AddWithValue("has_text", hasText);
        command.Parameters.AddWithValue("has_numeric", hasNumeric);
        command.Parameters.AddWithValue("numeric_query", numericValue);
        command.Parameters.AddWithValue("query_class", hints.QueryClassTag);
        command.Parameters.AddWithValue("take", take);
        command.Parameters.AddWithValue("candidate_limit", candidateLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SearchDocumentRecord(
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
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
