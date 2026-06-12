-- The 2026-06-11 journal-entry-line-tax-code-id migration added the per-line
-- Tax Code column for manual journal entries, but named it tax_code_id even
-- though (per its own comment) the value mirrors the bill_lines / quote_lines
-- tax_code_set_id additions — i.e. it stores a tax_code_sets bundle id, not a
-- legacy tax_codes rule id. Rename to say what it stores. No backfill needed:
-- the column is empty on every deployed host (verified 2026-06-11). Guarded so
-- a re-run, or a fresh install whose baseline already ships tax_code_set_id,
-- is a no-op.
do $$
begin
  if exists (select 1 from information_schema.columns
             where table_schema = 'public'
               and table_name = 'journal_entry_lines'
               and column_name = 'tax_code_id')
     and not exists (select 1 from information_schema.columns
             where table_schema = 'public'
               and table_name = 'journal_entry_lines'
               and column_name = 'tax_code_set_id') then
    alter table journal_entry_lines rename column tax_code_id to tax_code_set_id;
  end if;
end $$;
