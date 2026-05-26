-- Search Plan B: per-company query-intent cache.
--
-- The runtime search consults this table by (company_id, query_hash)
-- before running the candidate query. When a row exists with
-- status='ready', its entity_type_priors and expanded_terms feed two
-- new optional inputs into the search SQL:
--   * entity_type_priors → jsonb weight applied per-doc-entity-type
--     during scoring (caps below the deterministic exact/prefix tiers)
--   * expanded_terms → OR-extension of the candidate FTS gate so
--     synonyms (operator-coined OR AI-distilled) widen recall
--
-- Population is async / best-effort:
--   * On a cache miss the search engine enqueues a backfill task
--     (fire-and-forget). The current request still completes through
--     the PG-only path with full Plan A behavior.
--   * The backfill task calls IUnityAiGateway with the
--     UnitysearchQueryIntentV1 prompt and writes the result.
--   * If the AI gateway is disabled / failed, the backfill writes a
--     'failed' status row so we don't retry every search.
--
-- Isolation: every row is keyed by company_id. The search SQL filters
-- by (company_id, query_hash) so no cross-tenant intent ever leaks.
-- The unique constraint (company_id, query_hash) prevents thundering
-- herd when many users hit the same fresh query at once — the second
-- INSERT collides and skips the LLM call.
--
-- DEPLOYMENT NOTE: citus_app lacks DDL privileges. Apply with postgres
-- superuser BEFORE restarting accounting-api on the new build:
--
--     psql -U postgres -d citus_accounting \
--          -f deploy/migrations/2026-05-26-search-intent-cache.sql
--
-- Migration is idempotent — every CREATE uses IF NOT EXISTS.

create table if not exists unitysearch_query_intent_cache (
    id                  uuid          primary key default gen_random_uuid(),
    company_id          char(7)       not null references companies(id) on delete cascade,
    -- SHA-256 of normalized_query, base16-lowercased. Hashing instead
    -- of storing the raw query as the lookup key keeps the index
    -- small and gives us a fixed-width unique key — but we keep
    -- normalized_query in a separate column for audit / debugging.
    query_hash          char(64)      not null,
    normalized_query    text          not null,
    -- {"task": 0.7, "expense": 0.2} — keys MUST match doc.entity_type
    -- values used by the projection (validated client-side).
    entity_type_priors  jsonb         not null default '{}'::jsonb,
    -- ["shipping", "transport", "delivery"] — alternative phrasings.
    -- Used to OR-extend the FTS candidate gate. Empty array = no
    -- expansion.
    expanded_terms      text[]        not null default array[]::text[],
    confidence          numeric(4,3)  not null default 0,
    -- 'ai' (the gateway filled it), 'manual' (operator-curated, future),
    -- 'fallback' (deterministic, no LLM).
    source              varchar(16)   not null default 'ai',
    -- 'pending' (backfill in flight), 'ready' (usable), 'failed'
    -- (LLM/gateway error — don't re-trigger before TTL), 'stale'
    -- (expired but kept for audit until cleanup).
    status              varchar(16)   not null default 'pending',
    failure_reason      text          null,
    created_at          timestamptz   not null default now(),
    updated_at          timestamptz   not null default now(),
    expires_at          timestamptz   not null default (now() + interval '14 days'),
    constraint uq_intent_cache_query unique (company_id, query_hash),
    constraint chk_intent_cache_status
        check (status in ('pending','ready','failed','stale')),
    constraint chk_intent_cache_source
        check (source in ('ai','manual','fallback'))
);

-- Hot-path lookup: per-company query hash, gated to ready + unexpired.
-- The partial predicate keeps the index small even if the table fills
-- with TTL-expired or failed rows pending cleanup.
create index if not exists ix_intent_cache_lookup
    on unitysearch_query_intent_cache (company_id, query_hash)
    where status = 'ready';

-- Backfill-side helper: find pending rows that need retry / cleanup.
create index if not exists ix_intent_cache_status_expires
    on unitysearch_query_intent_cache (status, expires_at);
