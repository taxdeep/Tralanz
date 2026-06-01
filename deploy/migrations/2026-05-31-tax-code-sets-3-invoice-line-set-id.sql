-- Sales Tax redesign R4-sales (invoice): invoice_lines may reference a Tax
-- Code bundle (tax_code_sets) as an alternative to a single legacy Rule in
-- tax_code_id. When present, PostgresInvoiceDocumentRepository passes it to
-- the engine as TaxCodeSetId, which expands the bundle to its member Rules
-- and returns the combined (multi-tax) total. Nullable + no FK, matching the
-- existing loose tax_code_id reference. Idempotent.
alter table invoice_lines add column if not exists tax_code_set_id uuid;
