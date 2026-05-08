-- Stage-1.4: extract inline EnsureInventoryGradeBillLineColumnsAsync
-- from PostgresBillDocumentRepository.cs into a real migration.
-- Same pattern as 2026-05-08-invoice-line-inventory-columns.sql.

alter table bill_lines add column if not exists item_id                    uuid;
alter table bill_lines add column if not exists warehouse_id               uuid;
alter table bill_lines add column if not exists uom_code                   text;
alter table bill_lines add column if not exists quantity                   numeric(18,6);
alter table bill_lines add column if not exists unit_cost                  numeric(18,6);
alter table bill_lines add column if not exists purchase_order_id          uuid;
alter table bill_lines add column if not exists purchase_order_line_number integer;

create index if not exists ix_bill_lines_company_purchase_order_line
  on bill_lines (company_id, purchase_order_id, purchase_order_line_number);
