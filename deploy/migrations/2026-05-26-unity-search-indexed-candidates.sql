-- Strengthen UnitySearch candidate generation for company-scoped indexed search.
-- PostgreSQL stays the source of fast, permission-bounded candidates; AI can
-- rerank or enrich later, but is not required for normal search operation.

create extension if not exists pg_trgm;

create index if not exists ix_search_documents_company_entity_status
  on search_documents (company_id, entity_type, is_voided, is_active);

create index if not exists ix_search_documents_primary_text_trgm
  on search_documents using gin (lower(primary_text) gin_trgm_ops);

create index if not exists ix_search_documents_secondary_text_trgm
  on search_documents using gin (lower(secondary_text) gin_trgm_ops);

create index if not exists ix_search_documents_search_text_trgm
  on search_documents using gin (lower(search_text) gin_trgm_ops);
