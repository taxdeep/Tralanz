-- Search Plan A: PG-side retrieval upgrade.
--
-- The runtime search query gains three new candidate / scoring tiers:
--   * tsvector full-text (the GIN ix_search_documents_search_vector was
--     already populated by the projection but never queried)
--   * pg_trgm fuzzy / typo tolerance on primary_text and exact_code_norm
--   * unitysearch_alias_suggestions live-query lookup (the table was
--     populated by AI distillation but the values were never consulted)
--
-- All three additions stay STRICTLY inside the existing
-- company_id + required_permissions + visibility_scope envelope on
-- search_documents — they widen WHAT counts as a candidate match, never
-- escape company / permission isolation.
--
-- AI is not involved on the hot path. The migration installs pure-PG
-- infrastructure that benefits searches even when the AI gateway is
-- unreachable.
--
-- DEPLOYMENT NOTE: citus_app lacks DDL privileges. Apply with postgres
-- superuser BEFORE restarting accounting-api on the new build:
--
--     psql -U postgres -d citus_accounting \
--          -f deploy/migrations/2026-05-26-search-trgm-fts.sql
--
-- Migration is idempotent — every CREATE / INSERT uses IF NOT EXISTS.

-- ========================================================================
-- 1. Trigram extension (typo tolerance + similarity()/% operator)
-- ========================================================================

create extension if not exists pg_trgm;

-- ========================================================================
-- 2. Trigram GIN indexes for fuzzy match on the two operator-facing
--    fields. lower() is wrapped because the query normalises to
--    lowercase before the SQL compare, and pg_trgm is case-sensitive
--    without it. The partial predicate (not is_voided) keeps the
--    index small — voided rows never surface in any picker anyway.
-- ========================================================================

create index if not exists ix_search_documents_primary_trgm
    on search_documents using gin (lower(primary_text) gin_trgm_ops)
    where not is_voided;

create index if not exists ix_search_documents_code_trgm
    on search_documents using gin (lower(exact_code_norm) gin_trgm_ops)
    where exact_code_norm is not null and not is_voided;

-- ========================================================================
-- 3. Alias suggestions live-lookup index. The new LATERAL join in the
--    query filters by (company_id, normalized_alias, status='Active'),
--    so a composite index covering that key prefix keeps the per-row
--    join sub-millisecond.
-- ========================================================================

create index if not exists ix_unitysearch_alias_suggestions_lookup
    on unitysearch_alias_suggestions (company_id, normalized_alias, status)
    where status = 'Active';
