-- Runtime columns introduced after the migration runner became the source of
-- schema evolution. Keep request-time stores free of new inline ALTER TABLE.

ALTER TABLE company_module_flags
  ADD COLUMN IF NOT EXISTS access_expires_at timestamptz NULL;

ALTER TABLE inventory_documents
  ADD COLUMN IF NOT EXISTS document_number text NULL,
  ADD COLUMN IF NOT EXISTS client_request_hash text NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_inventory_documents_company_document_number
  ON inventory_documents (company_id, lower(document_number))
  WHERE document_number IS NOT NULL;

ALTER TABLE inventory_items
  ADD COLUMN IF NOT EXISTS base_uom_code text NULL,
  ADD COLUMN IF NOT EXISTS sales_uom_code text NULL,
  ADD COLUMN IF NOT EXISTS purchase_uom_code text NULL;
