-- Stage-1.4: extract inline EnsureInventoryGradeInvoiceLineColumnsAsync
-- (PostgresInvoiceDocumentRepository.cs) into a real migration. The
-- columns existed on the live remote since Stage 0 — added by the
-- repository's startup-time `_inventoryGradeColumnsEnsured` short-
-- circuit (commit 2ef2640). After this migration runs, the inline
-- helper is removed in the same commit.
--
-- IF NOT EXISTS makes this idempotent: the bootstrap runner records
-- it as already-applied on existing tenants where the columns are in
-- place; on a fresh tenant the runner applies it for real.

alter table invoice_lines add column if not exists item_id      uuid;
alter table invoice_lines add column if not exists warehouse_id uuid;
alter table invoice_lines add column if not exists uom_code     text;
