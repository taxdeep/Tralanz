-- Extracted from PostgresBillReceiptMatchingRepository.EnsureSchemaAsync.
-- Production deployments should apply this before disabling runtime
-- schema management in Accounting API.

create table if not exists bill_receipt_matching_allocations (
  id uuid primary key,
  company_id char(7) not null,
  vendor_id uuid not null,
  item_id uuid not null,
  warehouse_id uuid not null,
  uom_code text not null,
  bill_id uuid not null,
  bill_line_number integer not null,
  receipt_id uuid not null,
  receipt_line_number integer not null,
  matched_quantity numeric(18,6) not null,
  created_at timestamptz not null default now()
);

create table if not exists bill_receipt_matching_discrepancy_lanes (
  id uuid primary key,
  company_id char(7) not null,
  bill_id uuid not null,
  bill_line_number integer not null,
  discrepancy_type text not null,
  investigation_status text not null,
  item_id uuid not null,
  warehouse_id uuid not null,
  uom_code text not null,
  bill_quantity numeric(18,6) not null,
  covered_quantity numeric(18,6) not null,
  remaining_quantity numeric(18,6) not null,
  summary text not null,
  first_detected_at timestamptz not null,
  last_detected_at timestamptz not null
);

create unique index if not exists ux_bill_receipt_matching_allocations_natural
  on bill_receipt_matching_allocations (company_id, bill_id, bill_line_number, receipt_id, receipt_line_number);

create index if not exists ix_bill_receipt_matching_allocations_bill
  on bill_receipt_matching_allocations (company_id, bill_id, bill_line_number);

create index if not exists ix_bill_receipt_matching_allocations_receipt
  on bill_receipt_matching_allocations (company_id, receipt_id, receipt_line_number);

create index if not exists ix_bill_receipt_matching_allocations_anchor
  on bill_receipt_matching_allocations (company_id, vendor_id, item_id, warehouse_id, uom_code);

create unique index if not exists ux_bill_receipt_matching_discrepancy_lanes_natural
  on bill_receipt_matching_discrepancy_lanes (company_id, bill_id, bill_line_number, discrepancy_type);

create index if not exists ix_bill_receipt_matching_discrepancy_lanes_bill
  on bill_receipt_matching_discrepancy_lanes (company_id, bill_id, investigation_status);
