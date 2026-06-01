-- Drop the v2-component-model foreign keys from
-- document_line_sales_tax_snapshots so the Rule/Code (two-layer) tax model
-- can persist its per-rule snapshot legs.
--
-- The snapshot table was originally built for the v2 component model:
--   tax_code_id     -> sales_tax_codes(id)
--   component_id    -> sales_tax_code_components(id)
--   jurisdiction_id -> sales_tax_jurisdictions(id)   (NOT NULL)
--
-- The Rule/Code model (legacy tax_codes as Tax Rules + tax_code_sets as
-- Tax Codes) writes legs whose tax_code_id is a legacy tax_codes.id,
-- component_id is a tax_code_set_rules membership id, and jurisdiction_id
-- is the empty guid -- none of which exist in the v2 tables, so every
-- multi-rule (e.g. GST + PST) save hit a 23503 FK violation
-- (PostgreSqlTaxSnapshotPersister.InsertOneAsync -> SaveDraftAsync).
--
-- The snapshot is a self-contained immutable record: it denormalises the
-- code / name / rate / treatment / GL accounts at compute time, so these
-- referential FKs to mutable v2 reference tables are not needed. The
-- account FKs (payable / recoverable / non_recoverable) and the company
-- FK stay -- those still point at live, valid rows.
--
-- NOTE: already applied to the live DB by hand (postgres superuser) on
-- 2026-06-01 to unblock testing; this file makes it reproducible. The
-- DROP ... IF EXISTS clauses are idempotent, so re-running is a no-op.
alter table document_line_sales_tax_snapshots
  drop constraint if exists document_line_sales_tax_snapshots_tax_code_id_fkey;

alter table document_line_sales_tax_snapshots
  drop constraint if exists document_line_sales_tax_snapshots_component_id_fkey;

alter table document_line_sales_tax_snapshots
  drop constraint if exists document_line_sales_tax_snapshots_jurisdiction_id_fkey;
