-- Moves small inventory receipt / return runtime ALTERs into deploy-time
-- schema. Apply after inventory-foundation.

do $$
begin
  if to_regclass('inventory_documents') is not null then
    alter table inventory_documents
      add column if not exists document_number text null;

    create unique index if not exists ux_inventory_documents_company_document_number
      on inventory_documents (company_id, lower(document_number))
      where document_number is not null;
  end if;

  if to_regclass('inventory_document_lines') is not null then
    alter table inventory_document_lines
      add column if not exists condition_code text null;

    alter table inventory_document_lines
      add column if not exists return_reason_code text null;

    alter table inventory_document_lines
      add column if not exists disposition_reason_code text null;
  end if;
end $$;
