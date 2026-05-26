-- Search Plan C-Population: query-embedding cache column.
--
-- Extends unitysearch_query_intent_cache with the pgvector embedding
-- of the normalized query so the hot search path can re-use it on
-- subsequent hits for the same (company, query) pair. Populated by
-- the existing query-intent backfill task (Plan B) when the embedding
-- provider is configured + enabled; left NULL otherwise.
--
-- DEPLOYMENT NOTE: requires the pgvector extension (already installed
-- by 2026-05-26-search-pgvector-foundation.sql). Apply with postgres
-- superuser BEFORE restarting accounting-api.
--
-- Migration is idempotent.

alter table unitysearch_query_intent_cache
    add column if not exists query_embedding vector(1536);
