using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;
using Modules.Company.FeatureManagement;
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
        var hasNumeric = hints.IsNumeric;
        var numericValue = hints.NumericValue ?? 0m;

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
                doc.module_key,
                doc.required_permissions,
                doc.owner_user_id,
                doc.visibility_scope,
                doc.visibility_override_permission,
                (
                  -- L1 amount-exact (200) and L2 amount-tolerance (120) tiers.
                  -- Capped well above text-match scores so an amount hit
                  -- always outranks a text/code hit, regardless of priors.
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
                  -- Per-user query-class prior. Bounded by ln() so a
                  -- thousand clicks add ~55 points — strictly less than
                  -- the L1/L2 gap (80) so an amount-exact match on a
                  -- never-clicked entity still beats a tolerance match
                  -- on the user's favorite entity.
                  coalesce(ln(1 + prior.click_count::numeric) * 8, 0) +
                  case
                    when prior.last_clicked_at_utc >= now() - interval '30 day' then 4
                    else 0
                  end +
                  -- Cold-start default for numeric queries: bias toward
                  -- entities the user typically searches by amount when
                  -- they have no per-user prior yet. Authority comment in
                  -- this same SQL: numeric_decimal → JE > AR/AP > credits.
                  case
                    when @query_class = 'numeric_decimal' and doc.entity_type = 'journal_entry' then 8
                    when @query_class = 'numeric_decimal' and doc.entity_type in ('invoice','bill') then 5
                    when @query_class = 'numeric_decimal' and doc.entity_type in ('credit_note','vendor_credit') then 3
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
              left join search_query_class_priors prior
                on prior.company_id = doc.company_id
               and prior.user_id = @user_id
               and prior.query_class = @query_class
               and prior.entity_type = doc.entity_type
              where doc.company_id = @company_id
                and doc.entity_type = any(@entity_types)
                -- Voided / reversed documents are noise in every search
                -- context the app currently has (topbar, transactions
                -- search, all pickers). The projection writer sets
                -- is_voided=true for status in ('voided','reversed') on
                -- invoice / bill / credit_note / vendor_credit / journal_entry
                -- and 'cancelled' on PO, so this single guard hides them
                -- everywhere without per-policy bookkeeping. If a future
                -- audit-style surface needs to see them, add an opt-in
                -- flag — never make this unfiltered the default.
                and not doc.is_voided
                and (not @enforce_active_only or doc.is_active)
                and (not @enforce_business_eligibility or (doc.is_active and not doc.is_voided))
                -- Per-company module gate. Only "toggleable" module keys
                -- (the FeatureManagement catalog) go through the
                -- company_module_flags lookup; always-on modules like
                -- ar/ap/gl/inventory/core skip the join.
                and (
                  not (doc.module_key = any(@toggleable_module_keys))
                  or exists (
                    select 1 from company_module_flags f
                    where f.company_id = doc.company_id
                      and f.module_key = doc.module_key
                      and f.enabled = true
                  )
                )
                -- Permission gate. Empty required_permissions[] = no
                -- restriction (e.g. static "jump to" rows); non-empty
                -- requires at least one overlap with the caller's
                -- @user_permissions set.
                and (
                  coalesce(cardinality(doc.required_permissions), 0) = 0
                  or doc.required_permissions && @user_permissions
                )
                -- Visibility scope. 'assignee_only' rows (per-user
                -- task assignments etc.) hide unless owner_user_id
                -- matches the caller — OR the caller holds the row's
                -- visibility_override_permission (the
                -- "task.view.all"-style "I can see everyone's"
                -- token). Batch 7 added the override branch.
                and (
                  doc.visibility_scope = 'company'
                  or (doc.visibility_scope = 'assignee_only' and doc.owner_user_id = @user_id)
                  or (doc.visibility_scope = 'assignee_only'
                      and doc.visibility_override_permission is not null
                      and doc.visibility_override_permission = any(@user_permissions))
                )
                and (
                  doc.exact_code_norm = @normalized_query
                  or doc.exact_code_norm like @exact_prefix
                  or lower(doc.primary_text) like @text_contains
                  or lower(doc.secondary_text) like @text_contains
                  or lower(doc.search_text) like @text_contains
                  or (
                    @has_numeric
                    and doc.amount is not null
                    and (doc.amount = @numeric_query or abs(doc.amount - @numeric_query) < 0.005)
                  )
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
              computed_score,
              module_key,
              required_permissions,
              owner_user_id,
              visibility_scope,
              visibility_override_permission
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
        command.Parameters.AddWithValue("exact_prefix", $"{normalizedQuery}%");
        command.Parameters.AddWithValue("text_prefix", $"{normalizedQuery}%");
        command.Parameters.AddWithValue("text_contains", $"%{normalizedQuery}%");
        command.Parameters.AddWithValue("has_numeric", hasNumeric);
        command.Parameters.AddWithValue("numeric_query", numericValue);
        command.Parameters.AddWithValue("query_class", hints.QueryClassTag);
        command.Parameters.AddWithValue("take", take);
        // Single source of truth for "which module keys are toggleable"
        // — sourced from the FeatureManagement catalog so adding a new
        // toggleable module is purely a C# change.
        command.Parameters.AddWithValue("toggleable_module_keys", CompanyModuleFlagCatalog.KnownKeys.ToArray());
        command.Parameters.AddWithValue("user_permissions", query.Permissions.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ownerOrdinal = reader.GetOrdinal("owner_user_id");
            UserId? ownerUserId = reader.IsDBNull(ownerOrdinal)
                ? null
                : UserId.Parse(reader.GetString(ownerOrdinal).Trim());
            var requiredPermissionsOrdinal = reader.GetOrdinal("required_permissions");
            IReadOnlyList<string> requiredPermissions = reader.IsDBNull(requiredPermissionsOrdinal)
                ? Array.Empty<string>()
                : reader.GetFieldValue<string[]>(requiredPermissionsOrdinal);
            var overridePermissionOrdinal = reader.GetOrdinal("visibility_override_permission");
            string? visibilityOverridePermission = reader.IsDBNull(overridePermissionOrdinal)
                ? null
                : reader.GetString(overridePermissionOrdinal).Trim();

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
                reader.GetDecimal(reader.GetOrdinal("computed_score")),
                reader.GetString(reader.GetOrdinal("module_key")),
                requiredPermissions,
                ownerUserId,
                reader.GetString(reader.GetOrdinal("visibility_scope")),
                visibilityOverridePermission));
        }

        return results;
    }
}
