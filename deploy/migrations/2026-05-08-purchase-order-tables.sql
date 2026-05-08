-- Stage-1.4: extract PostgresPurchaseOrderDocumentRepository.EnsureSchemaAsync.
-- Same SQL block, now applied at deploy time by the migration runner.
-- The inline EnsureSchemaAsync still exists for fresh test databases
-- but caches its result via _schemaEnsured after the first call so it
-- can never re-emerge as a hot-path AccessExclusiveLock source.

create table if not exists purchase_orders (
  id uuid primary key,
  company_id char(7) not null,
  entity_number char(11) not null,
  purchase_order_number text not null,
  vendor_id uuid not null,
  status text not null,
  order_date date not null,
  expected_date date null,
  vendor_reference text null,
  memo text null,
  created_by_user_id char(7) not null,
  created_at timestamptz not null default now(),
  updated_by_user_id char(7) null,
  updated_at timestamptz not null default now(),
  approved_by_user_id char(7) null,
  approved_at timestamptz null,
  issued_by_user_id char(7) null,
  issued_at timestamptz null,
  closed_by_user_id char(7) null,
  closed_at timestamptz null,
  cancelled_by_user_id char(7) null,
  cancelled_at timestamptz null,
  amendment_started_by_user_id char(7) null,
  amendment_started_at timestamptz null
);

alter table purchase_orders add column if not exists approved_by_user_id          char(7)     null;
alter table purchase_orders add column if not exists approved_at                  timestamptz null;
alter table purchase_orders add column if not exists closed_by_user_id            char(7)     null;
alter table purchase_orders add column if not exists closed_at                    timestamptz null;
alter table purchase_orders add column if not exists cancelled_by_user_id         char(7)     null;
alter table purchase_orders add column if not exists cancelled_at                 timestamptz null;
alter table purchase_orders add column if not exists amendment_started_by_user_id char(7)     null;
alter table purchase_orders add column if not exists amendment_started_at         timestamptz null;

create unique index if not exists ux_purchase_orders_company_entity_number
  on purchase_orders (company_id, entity_number);
create unique index if not exists ux_purchase_orders_company_purchase_order_number
  on purchase_orders (company_id, purchase_order_number);
create index if not exists ix_purchase_orders_company_order_date
  on purchase_orders (company_id, order_date desc, created_at desc);

create table if not exists purchase_order_lines (
  id uuid primary key,
  company_id char(7) not null,
  purchase_order_id uuid not null,
  line_number integer not null,
  item_id uuid not null,
  ordered_quantity numeric(18,6) not null,
  uom_code text not null,
  description text null,
  unit_cost numeric(18,6) null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create unique index if not exists ux_purchase_order_lines_company_order_line
  on purchase_order_lines (company_id, purchase_order_id, line_number);
create index if not exists ix_purchase_order_lines_company_order
  on purchase_order_lines (company_id, purchase_order_id, line_number);

create table if not exists purchase_order_quantity_discrepancy_lanes (
  id uuid primary key,
  company_id char(7) not null,
  purchase_order_id uuid not null,
  purchase_order_line_number integer not null,
  discrepancy_type text not null,
  investigation_status text not null,
  item_id uuid not null,
  uom_code text not null,
  ordered_quantity numeric(18,6) not null,
  received_quantity numeric(18,6) not null,
  billed_quantity numeric(18,6) not null,
  remaining_to_receive_quantity numeric(18,6) not null,
  remaining_to_bill_quantity numeric(18,6) not null,
  summary text not null,
  first_detected_at timestamptz not null,
  last_detected_at timestamptz not null default now(),
  review_note text null,
  reviewed_by_user_id char(7) null,
  reviewed_at timestamptz null,
  refreshed_by_user_id char(7) not null
);

alter table purchase_order_quantity_discrepancy_lanes
  add column if not exists review_note         text        null;
alter table purchase_order_quantity_discrepancy_lanes
  add column if not exists reviewed_by_user_id char(7)     null;
alter table purchase_order_quantity_discrepancy_lanes
  add column if not exists reviewed_at         timestamptz null;

alter table purchase_order_quantity_discrepancy_lanes
  drop constraint if exists ck_purchase_order_quantity_discrepancy_lanes_type;
alter table purchase_order_quantity_discrepancy_lanes
  add constraint ck_purchase_order_quantity_discrepancy_lanes_type
    check (discrepancy_type in ('over_received', 'over_billed', 'billed_ahead_of_received'));

alter table purchase_order_quantity_discrepancy_lanes
  drop constraint if exists ck_purchase_order_quantity_discrepancy_lanes_status;
alter table purchase_order_quantity_discrepancy_lanes
  add constraint ck_purchase_order_quantity_discrepancy_lanes_status
    check (investigation_status in ('open', 'resolved', 'override_authorized'));

create unique index if not exists ux_purchase_order_quantity_discrepancy_lanes_natural
  on purchase_order_quantity_discrepancy_lanes (company_id, purchase_order_id, purchase_order_line_number, discrepancy_type);
create index if not exists ix_purchase_order_quantity_discrepancy_lanes_open
  on purchase_order_quantity_discrepancy_lanes (company_id, purchase_order_id, investigation_status);

do $$
begin
  if to_regclass('receipt_lines') is not null then
    alter table receipt_lines add column if not exists purchase_order_id          uuid    null;
    alter table receipt_lines add column if not exists purchase_order_line_number integer null;
    create index if not exists ix_receipt_lines_company_purchase_order_line
      on receipt_lines (company_id, purchase_order_id, purchase_order_line_number);
  end if;

  if to_regclass('bill_lines') is not null then
    alter table bill_lines add column if not exists purchase_order_id          uuid    null;
    alter table bill_lines add column if not exists purchase_order_line_number integer null;
    create index if not exists ix_bill_lines_company_purchase_order_line
      on bill_lines (company_id, purchase_order_id, purchase_order_line_number);
  end if;
end $$;
