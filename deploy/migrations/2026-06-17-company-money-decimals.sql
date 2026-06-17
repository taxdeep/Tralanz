-- Per-company money decimal precision (2 default, or 3). Drives the central
-- money formatter (and, in a later phase, posting/rounding precision) so a
-- company that trades in 3-decimal currencies can opt in. not-null default 2
-- => every existing row backfills to 2, preserving current behaviour. The
-- accounting API also adds this column on startup via EnsureSchemaAsync; this
-- migration keeps the formal deploy path in sync.
alter table companies add column if not exists money_decimals smallint not null default 2;

alter table companies drop constraint if exists companies_money_decimals_chk;
alter table companies add constraint companies_money_decimals_chk
  check (money_decimals in (2, 3));
