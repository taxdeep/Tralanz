-- Stage-1.4 batch 3: extract PostgresOpenItemAdjustmentAccountMappingRepository.EnsureSchemaAsync.
-- Same SQL applied at deploy time. The inline helper still exists
-- (cached via _schemaEnsured) so fresh test databases keep working.

create table if not exists open_item_adjustment_account_mappings (
  id uuid primary key,
  company_id char(7) not null,
  book_id uuid null,
  open_item_type text not null,
  adjustment_type text not null,
  adjustment_account_id uuid not null,
  is_active boolean not null default true,
  created_by_user_id char(7) null,
  updated_by_user_id char(7) null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  deactivated_at timestamptz null
);

alter table open_item_adjustment_account_mappings
  add column if not exists book_id uuid null,
  add column if not exists created_by_user_id char(7) null,
  add column if not exists updated_by_user_id char(7) null,
  add column if not exists deactivated_at timestamptz null;

do $$
begin
  if not exists (
    select 1
    from pg_constraint
    where conname = 'ck_open_item_adjustment_mapping_open_item_type'
  ) then
    alter table open_item_adjustment_account_mappings
      add constraint ck_open_item_adjustment_mapping_open_item_type
      check (lower(open_item_type) in ('ar_open_item', 'ap_open_item'));
  end if;

  if not exists (
    select 1
    from pg_constraint
    where conname = 'ck_open_item_adjustment_mapping_adjustment_type'
  ) then
    alter table open_item_adjustment_account_mappings
      add constraint ck_open_item_adjustment_mapping_adjustment_type
      check (lower(adjustment_type) in ('write_off', 'small_balance_adjustment'));
  end if;
end $$;

create index if not exists ix_open_item_adjustment_account_mappings_company_lookup
  on open_item_adjustment_account_mappings (
    company_id,
    open_item_type,
    adjustment_type,
    book_id,
    is_active
  );

create unique index if not exists ux_open_item_adjustment_account_mappings_active_scope
  on open_item_adjustment_account_mappings (
    company_id,
    lower(open_item_type),
    lower(adjustment_type),
    coalesce(book_id, '00000000-0000-0000-0000-000000000000'::uuid)
  )
  where is_active = true;
