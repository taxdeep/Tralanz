-- Persist the per-line Tax Code selected on manual journal entries. The JE
-- post path (PostgreSqlJournalEntryPostingStore) previously dropped per-line
-- tax; this adds the column it writes to. Nullable, no FK — mirrors the recent
-- bill_lines / quote_lines tax_code_set_id additions and avoids cross-version
-- FK fragility. Idempotent (re-running adds nothing).
alter table journal_entry_lines add column if not exists tax_code_id uuid;
