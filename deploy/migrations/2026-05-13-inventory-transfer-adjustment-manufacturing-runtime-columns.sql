-- Moves inventory transfer / adjustment / manufacturing runtime ALTERs into
-- deploy-time schema. Apply after inventory-foundation.

do $$
begin
  if to_regclass('warehouse_transfer_lines') is not null then
    alter table warehouse_transfer_lines
      add column if not exists uom_code text;

    update warehouse_transfer_lines
    set uom_code = coalesce(nullif(trim(uom_code), ''), 'EA')
    where uom_code is null
       or btrim(uom_code) = '';
  end if;

  if to_regclass('warehouse_transfers') is not null then
    alter table warehouse_transfers
      add column if not exists submitted_at timestamptz null;

    alter table warehouse_transfers
      add column if not exists submitted_by_user_id char(7) null;

    alter table warehouse_transfers
      add column if not exists shipped_by_user_id char(7) null;

    alter table warehouse_transfers
      add column if not exists received_by_user_id char(7) null;
  end if;

  if to_regclass('inventory_documents') is not null then
    alter table inventory_documents
      add column if not exists document_number text null;

    alter table inventory_documents
      add column if not exists approved_at timestamptz null;

    alter table inventory_documents
      add column if not exists approved_by_user_id char(7) null;

    alter table inventory_documents
      add column if not exists posted_by_user_id char(7) null;

    alter table inventory_documents
      add column if not exists client_request_hash text null;

    create unique index if not exists ux_inventory_documents_company_document_number
      on inventory_documents (company_id, lower(document_number))
      where document_number is not null;
  end if;
end $$;
