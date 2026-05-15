-- Move platform identity sequence table creation out of runtime allocators.
-- Apply with a migration/admin role before running app services with
-- SchemaManagement:ApplyOnStartup disabled and without DDL privileges.

create table if not exists platform_user_id_sequence (
  singleton_key boolean primary key default true,
  next_ordinal bigint not null,
  check (singleton_key = true)
);

insert into platform_user_id_sequence (singleton_key, next_ordinal)
values (true, 1)
on conflict (singleton_key) do nothing;

create table if not exists platform_company_id_sequence (
  singleton_key boolean primary key default true,
  next_ordinal bigint not null,
  check (singleton_key = true)
);

insert into platform_company_id_sequence (singleton_key, next_ordinal)
values (true, 1)
on conflict (singleton_key) do nothing;

create table if not exists company_entity_number_sequences (
  company_id char(7) not null,
  entity_year integer not null,
  next_ordinal bigint not null,
  primary key (company_id, entity_year)
);
