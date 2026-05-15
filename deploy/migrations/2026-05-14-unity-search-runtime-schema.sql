-- Move UnitySearch runtime-created tables into migration-managed schema.
-- Production application roles should not need DDL privileges when users
-- open the search box, record recent searches, or click search results.

create table if not exists search_documents (
  company_id char(7) not null,
  entity_type text not null,
  source_id uuid not null,
  group_key text not null,
  primary_text text not null,
  secondary_text text not null default '',
  search_text text not null,
  search_vector tsvector null,
  exact_code_norm text not null default '',
  navigation_href text not null default '',
  metadata_json jsonb not null default '{}'::jsonb,
  effective_date date null,
  amount numeric(18, 6) null,
  is_active boolean not null default true,
  is_voided boolean not null default false,
  rank_boost numeric(18, 6) not null default 0,
  version bigint not null default 1,
  updated_at timestamptz not null default now(),
  primary key (company_id, entity_type, source_id)
);

alter table search_documents
  add column if not exists group_key text not null default '',
  add column if not exists primary_text text not null default '',
  add column if not exists secondary_text text not null default '',
  add column if not exists search_text text not null default '',
  add column if not exists search_vector tsvector null,
  add column if not exists exact_code_norm text not null default '',
  add column if not exists navigation_href text not null default '',
  add column if not exists metadata_json jsonb not null default '{}'::jsonb,
  add column if not exists effective_date date null,
  add column if not exists amount numeric(18, 6) null,
  add column if not exists is_active boolean not null default true,
  add column if not exists is_voided boolean not null default false,
  add column if not exists rank_boost numeric(18, 6) not null default 0,
  add column if not exists version bigint not null default 1,
  add column if not exists updated_at timestamptz not null default now();

create index if not exists ix_search_documents_company_group
  on search_documents (company_id, group_key, entity_type);

create index if not exists ix_search_documents_company_exact_code
  on search_documents (company_id, exact_code_norm);

create index if not exists ix_search_documents_search_vector
  on search_documents using gin (search_vector);

create index if not exists ix_search_documents_company_amount
  on search_documents (company_id, amount)
  where amount is not null;

create table if not exists search_query_class_priors (
  company_id char(7) not null,
  user_id char(7) not null,
  query_class text not null,
  entity_type text not null,
  click_count bigint not null default 0,
  last_clicked_at_utc timestamptz null,
  primary key (company_id, user_id, query_class, entity_type)
);

create index if not exists ix_search_query_class_priors_lookup
  on search_query_class_priors (company_id, user_id, query_class);

create table if not exists search_recent_queries (
  company_id char(7) not null,
  user_id char(7) not null,
  context text not null,
  query_text text not null,
  used_at_utc timestamptz not null,
  primary key (company_id, user_id, context, query_text)
);

create index if not exists ix_search_recent_queries_lookup
  on search_recent_queries (company_id, user_id, context, used_at_utc desc);

create table if not exists search_click_stats (
  company_id char(7) not null,
  user_id char(7) not null,
  context text not null,
  entity_type text not null,
  source_id uuid not null,
  click_count integer not null default 0,
  last_clicked_at_utc timestamptz not null,
  primary key (company_id, user_id, context, entity_type, source_id)
);

create index if not exists ix_search_click_stats_lookup
  on search_click_stats (company_id, user_id, context, last_clicked_at_utc desc);
