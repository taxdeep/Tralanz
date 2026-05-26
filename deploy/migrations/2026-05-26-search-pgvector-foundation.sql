-- Search Plan C foundation: pgvector + embedding column on search_documents.
--
-- THIS MIGRATION IS PURE INFRASTRUCTURE. It does NOT change search
-- behaviour on its own — the new embedding column is nullable, every
-- doc starts with NULL, and the search SQL's vector clauses are double-
-- gated on both `doc.embedding IS NOT NULL` and `@query_embedding IS
-- NOT NULL`. Until the follow-up batch wires up the embedding provider
-- + back-fill job, every search request continues to run full Plan
-- A+B behaviour.
--
-- Embedding dimension fixed at 1536 — matches OpenAI text-embedding-3-
-- small's default output and stays well under the pgvector HNSW limit
-- (2000 dims as of 0.6).
--
-- DEPLOYMENT NOTE: requires the postgresql-16-pgvector apt package on
-- the PG server. citus_app lacks DDL privileges. Apply with postgres
-- superuser BEFORE restarting accounting-api on the new build:
--
--     apt-get install -y postgresql-16-pgvector
--     psql -U postgres -d citus_accounting \
--          -f deploy/migrations/2026-05-26-search-pgvector-foundation.sql
--
-- Migration is idempotent — CREATE EXTENSION / ADD COLUMN / CREATE
-- INDEX all use IF NOT EXISTS.

-- ========================================================================
-- 1. pgvector extension
-- ========================================================================

create extension if not exists vector;

-- ========================================================================
-- 2. Embedding column (nullable). All existing rows get NULL — semantic
--    recall is no-op until the back-fill job populates them.
-- ========================================================================

alter table search_documents
    add column if not exists embedding vector(1536);

-- ========================================================================
-- 3. HNSW index for fast approximate cosine nearest-neighbour. Partial
--    predicate keeps the index small when most rows are still NULL.
--    cosine_ops matches OpenAI's text-embedding-3-* output, which is
--    L2-normalized — `embedding <=> @query` returns cosine distance
--    in [0, 2] (0 = identical, 2 = opposite).
--
--    HNSW build parameters: m=16, ef_construction=64 — pgvector defaults
--    that give a good balance of recall vs build cost on the moderate
--    row counts a per-company search projection sees.
-- ========================================================================

create index if not exists ix_search_documents_embedding_hnsw
    on search_documents using hnsw (embedding vector_cosine_ops)
    with (m = 16, ef_construction = 64)
    where embedding is not null and not is_voided;

-- ========================================================================
-- 4. Helper btree for the back-fill job (next batch) to find rows that
--    still need an embedding. Partial predicate keeps it tiny.
-- ========================================================================

create index if not exists ix_search_documents_embedding_pending
    on search_documents (company_id, entity_type)
    where embedding is null and not is_voided;
