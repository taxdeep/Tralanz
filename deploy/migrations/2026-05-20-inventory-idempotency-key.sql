-- =====================================================================
-- PR-5 (C-4): Inventory POST idempotency
-- =====================================================================
--
-- Adds `idempotency_key` to `inventory_documents` so a network-retried
-- Receipt / Issue / Shipment / Transfer cannot double-insert the same
-- movement. The partial unique index enforces uniqueness per company
-- when the key is non-null; rows without a key (legacy, internal
-- maintenance ops) are unaffected.
--
-- The client passes the key via the `Idempotency-Key` HTTP header.
-- A retried POST with the same key races on the unique index → the
-- store catches the 23505 violation and replays a thin summary
-- (existing document_id + document_number) rather than running the
-- side-effecting writes a second time.
--
-- All four document types (purchase_receipt, sales_issue, shipment,
-- transfer_ship, transfer_receive) write to the same
-- `inventory_documents` table, so a single column + index covers them
-- all.
-- =====================================================================

begin;

alter table inventory_documents
  add column if not exists idempotency_key text;

create unique index if not exists ux_inventory_documents_idempotency
  on inventory_documents(company_id, idempotency_key)
  where idempotency_key is not null;

comment on column inventory_documents.idempotency_key is
  'Client-supplied opaque token (Idempotency-Key HTTP header). Unique per company while non-null. PR-5 / C-4.';

commit;
