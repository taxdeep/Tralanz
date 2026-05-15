-- Installs the company-scoped display numbering table used by draft and
-- posting flows. Runtime UI code must not create this table on demand.

create table if not exists company_numbering_sequences (
  company_id char(7) not null references companies(id) on delete cascade,
  scope_key text not null,
  prefix text not null,
  next_number bigint not null,
  padding smallint not null,
  suggestion_enabled boolean not null default true,
  updated_at timestamptz not null default now(),
  primary key (company_id, scope_key)
);

