using System.Text.Json;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;
using Modules.Company.FeatureManagement;
using Npgsql;
using NpgsqlTypes;

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
            -- caller_info: resolve once whether the caller is the company
            -- Owner (implied-all-permissions) and pull the active business-
            -- permission grants from company_user_permissions. We
            -- materialize so the multiple references in the permission /
            -- visibility WHERE clauses each see a stable snapshot without
            -- re-running the joins.
            with caller_info as materialized (
              select
                coalesce(
                  (select is_owner
                     from company_memberships
                    where company_id = @company_id
                      and user_id = @user_id
                      and is_active = true),
                  false
                ) as is_owner,
                coalesce(
                  (select array_agg(cup.permission_token)
                     from company_user_permissions cup
                     join permission_registry pr
                       on pr.permission_token = cup.permission_token
                     join company_memberships m
                       on m.company_id = cup.company_id
                      and m.user_id = cup.user_id
                    where cup.company_id = @company_id
                      and cup.user_id = @user_id
                      and cup.is_active = true
                      and pr.is_assignable = true
                      and m.is_active = true),
                  array[]::text[]
                ) as granted_tokens
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
                  -- Plan A: tsvector full-text. The existing
                  -- ix_search_documents_search_vector GIN index catches
                  -- stem / token-order matches the LIKE patterns miss
                  -- ("invoices" vs "invoice", "ACME ON Tax" vs "Tax ACME ON").
                  -- websearch_to_tsquery handles raw user input safely. Score
                  -- 22 — below "primary_text contains" (28) so exact phrase
                  -- still wins when both fire.
                  case
                    when @normalized_query <> ''
                         and doc.search_vector is not null
                         and doc.search_vector @@ websearch_to_tsquery('simple', @normalized_query)
                      then 22
                    else 0
                  end +
                  -- Plan A: pg_trgm fuzzy / typo tolerance. similarity()
                  -- catches 1-2 char drift ("shippng" → "shipping",
                  -- "ofice" → "office"). Scored linearly with similarity,
                  -- capped at 18 — below tsvector (22) so an FTS-clean
                  -- match always beats a fuzzy match. Guarded on
                  -- length>=3 because pg_trgm is meaningless on tiny
                  -- input. The candidate gate below uses the % operator
                  -- so PG can pick the trigram GIN index.
                  case
                    when length(@normalized_query) >= 3
                         and lower(doc.primary_text) % @normalized_query
                      then least(18, (similarity(lower(doc.primary_text), @normalized_query) * 25)::int)
                    else 0
                  end +
                  -- Plan A: alias hit. unitysearch_alias_suggestions is
                  -- populated by the AI distillation job + (future)
                  -- operator curation. An explicit alias is a stronger
                  -- signal than generic FTS / fuzzy, so cap 30 (above
                  -- both). Confidence < 1 scales linearly. Lateral join
                  -- below joins by (company_id, entity_type, entity_id,
                  -- normalized_alias) so company isolation is preserved.
                  coalesce(alias_hit.alias_boost, 0) +
                  -- Plan B: AI-distilled entity-type prior. The intent
                  -- cache stores {"task": 0.7, ...}; if doc.entity_type
                  -- is a key, the weight (0..1) times 25 is added.
                  -- Capped at 25 so the prior nudges close ties but
                  -- never overrides a clean text / code match. Empty
                  -- jsonb '{}' → no contribution (the ->> lookup is null).
                  case
                    when @intent_priors is not null
                         and @intent_priors::jsonb ? doc.entity_type
                      then least(25, ((@intent_priors::jsonb ->> doc.entity_type)::numeric * 25)::int)
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
              -- Plan A: alias-suggestion lookup. Lateral join keyed by
              -- (company_id, entity_type, entity_id, normalized_alias)
              -- so the company filter is structurally enforced — alias
              -- rows from other tenants cannot leak in. Active +
              -- non-failed validation only. Top-confidence row wins
              -- when multiple aliases collide on the same target.
              left join lateral (
                select (a.confidence * 30)::int as alias_boost
                from unitysearch_alias_suggestions a
                where a.company_id = doc.company_id
                  and a.entity_type = doc.entity_type
                  and a.entity_id = doc.source_id
                  and a.normalized_alias = @normalized_query
                  and a.status = 'Active'
                  and (a.validation_status is null or a.validation_status <> 'Invalid')
                order by a.confidence desc
                limit 1
              ) alias_hit on true
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
                -- Permission gate. Owner bypasses (implied-all-
                -- permissions); else: empty required_permissions[] = no
                -- restriction (static "jump to" rows); non-empty requires
                -- at least one overlap with the caller's active grants
                -- from company_user_permissions. NOTE: granted_tokens is
                -- now sourced fresh from the table — NOT from the legacy
                -- session.Roles jsonb cache — so a revocation takes
                -- effect immediately on the next search.
                and (
                  (select is_owner from caller_info) = true
                  or coalesce(cardinality(doc.required_permissions), 0) = 0
                  or doc.required_permissions && (select granted_tokens from caller_info)
                )
                -- Visibility scope. Owner bypasses (sees all assignee_only
                -- rows). Otherwise: 'assignee_only' rows hide unless
                -- owner_user_id matches the caller — OR the caller holds
                -- the row's visibility_override_permission (e.g.
                -- task.view.all) as an active grant. The override token
                -- is also sourced from granted_tokens, not session.Roles.
                and (
                  (select is_owner from caller_info) = true
                  or doc.visibility_scope = 'company'
                  or (doc.visibility_scope = 'assignee_only' and doc.owner_user_id = @user_id)
                  or (doc.visibility_scope = 'assignee_only'
                      and doc.visibility_override_permission is not null
                      -- X-4: wrap in EXISTS so `any(granted_tokens)` parses as
                      -- the array-operator form (`text = any(text[])`). Without
                      -- this PostgreSQL reads `any((select granted_tokens from ...))`
                      -- as the subquery form, comparing `text = text[]` row-by-
                      -- row and failing with 42883 (no such operator).
                      and exists (
                        select 1 from caller_info ci
                        where doc.visibility_override_permission = any(ci.granted_tokens)
                      ))
                )
                -- Candidate gate: a doc must trigger at least one of these
                -- match conditions to be considered. All conditions sit
                -- inside the same AND-envelope as the company / permission /
                -- visibility gates above, so widening the candidate set
                -- here never bypasses isolation — only changes WHAT counts
                -- as a hit on rows the caller is already allowed to see.
                and (
                  doc.exact_code_norm = @normalized_query
                  or doc.exact_code_norm like @exact_prefix
                  or lower(doc.primary_text) like @text_contains
                  or lower(doc.secondary_text) like @text_contains
                  or lower(doc.search_text) like @text_contains
                  -- Plan A: tsvector FTS pulls in stem / token-order matches.
                  or (
                    @normalized_query <> ''
                    and doc.search_vector is not null
                    and doc.search_vector @@ websearch_to_tsquery('simple', @normalized_query)
                  )
                  -- Plan A: fuzzy / typo via pg_trgm. Threshold defaults
                  -- to ~0.3 (set_limit / pg_trgm.similarity_threshold);
                  -- the trigram GIN index picks up % at exactly that
                  -- threshold so this stays index-served.
                  or (
                    length(@normalized_query) >= 3
                    and lower(doc.primary_text) % @normalized_query
                  )
                  -- Plan A: alias hit. alias_hit is the lateral join above —
                  -- non-null means an Active alias row matched the query
                  -- for the (company, entity_type, entity_id) of this doc.
                  or alias_hit.alias_boost is not null
                  -- Plan B: AI-distilled synonym hit. @expanded_terms is
                  -- the operator-coined / LLM-suggested list of
                  -- alternative phrasings for the original query. We
                  -- unnest() it and OR each term's FTS match against the
                  -- doc's search_vector — any one hit qualifies the doc
                  -- as a candidate. Empty array → EXISTS over empty
                  -- subquery → false → no contribution. Note the array
                  -- is per-call, sized <= 10 by the validator, so the
                  -- inner EXISTS is bounded.
                  or (
                    @expanded_terms is not null
                    and cardinality(@expanded_terms) > 0
                    and doc.search_vector is not null
                    and exists (
                      select 1 from unnest(@expanded_terms) t
                      where t <> ''
                        and doc.search_vector @@ websearch_to_tsquery('simple', t)
                    )
                  )
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

        // Plan B: per-call intent payload from the query-intent cache.
        // Both parameters are nullable / empty by default so when no
        // cache hit exists (or AI is disabled / unreachable) the SQL
        // ranker falls through to Plan A behaviour. Empty / null
        // contributes zero score and matches zero candidates.
        var intentPriorsParam = command.Parameters.Add("intent_priors", NpgsqlDbType.Text);
        intentPriorsParam.Value = hints.Intent is { EntityTypePriors.Count: > 0 }
            ? (object)JsonSerializer.Serialize(hints.Intent.EntityTypePriors)
            : DBNull.Value;
        var expandedTermsParam = command.Parameters.Add("expanded_terms", NpgsqlDbType.Array | NpgsqlDbType.Text);
        expandedTermsParam.Value = (hints.Intent?.ExpandedTerms ?? Array.Empty<string>()).ToArray();
        // Single source of truth for "which module keys are toggleable"
        // — sourced from the FeatureManagement catalog so adding a new
        // toggleable module is purely a C# change.
        command.Parameters.AddWithValue("toggleable_module_keys", CompanyModuleFlagCatalog.KnownKeys.ToArray());
        // NOTE: PR-4D removed the @user_permissions parameter — the WHERE
        // clause now reads from the materialized caller_info CTE, which
        // pulls grants fresh from company_user_permissions on every
        // query. query.Permissions (sourced from session.Roles) is no
        // longer consulted here; we leave it on the contract so legacy
        // callers don't break, but the search SQL ignores it.

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
