-- Give quotes and sales orders an entity_number (the shared per-company EN
-- series), so every document carries the universal internal handle. They
-- still do NOT post to the GL / have no journal entry. New rows reserve a
-- number on create (store code); existing rows are backfilled separately.
begin;

alter table quotes add column if not exists entity_number char(11);
alter table sales_orders add column if not exists entity_number char(11);

create unique index if not exists uq_quotes_company_entity_number
    on quotes (company_id, entity_number);
create unique index if not exists uq_sales_orders_company_entity_number
    on sales_orders (company_id, entity_number);

commit;
