-- Moves inventory issue / shipment runtime ALTERs into deploy-time schema.
-- Apply after inventory-foundation.

do $$
begin
  if to_regclass('inventory_documents') is not null then
    alter table inventory_documents
      add column if not exists document_number text null;

    alter table inventory_documents
      add column if not exists carrier_name text null;

    alter table inventory_documents
      add column if not exists tracking_number text null;

    alter table inventory_documents
      add column if not exists shipping_slip_number text null;

    alter table inventory_documents
      drop constraint if exists ck_inventory_documents_document_type;

    alter table inventory_documents
      add constraint ck_inventory_documents_document_type
        check (document_type in (
          'purchase_receipt',
          'customer_return_receipt',
          'transfer_receive',
          'manufacturing_receipt',
          'opening_balance_receipt',
          'inventory_adjustment_gain',
          'sales_issue',
          'vendor_return_issue',
          'transfer_ship',
          'manufacturing_issue',
          'inventory_write_off',
          'inventory_adjustment_loss',
          'shipment'
        ));

    create unique index if not exists ux_inventory_documents_company_document_number
      on inventory_documents (company_id, lower(document_number))
      where document_number is not null;
  end if;

  create table if not exists inventory_outbound_matching_lanes (
    id uuid primary key,
    company_id char(7) not null,
    lane_type text not null,
    source_document_id uuid not null,
    item_id uuid not null,
    warehouse_id uuid not null,
    uom_code text not null,
    source_line_count integer not null,
    source_quantity numeric(18,6) not null,
    matched_document_count integer not null,
    matched_quantity numeric(18,6) not null,
    remaining_quantity numeric(18,6) not null,
    status text not null,
    latest_matched_at timestamptz null,
    updated_at timestamptz not null
  );

  alter table inventory_outbound_matching_lanes
    drop constraint if exists ck_inventory_outbound_matching_lanes_lane_type;

  alter table inventory_outbound_matching_lanes
    add constraint ck_inventory_outbound_matching_lanes_lane_type
      check (lane_type in ('invoice_shipment', 'shipment_issue'));

  create unique index if not exists ux_inventory_outbound_matching_lanes_natural
    on inventory_outbound_matching_lanes (company_id, lane_type, source_document_id, item_id, warehouse_id, uom_code);

  create table if not exists inventory_outbound_discrepancy_lanes (
    id uuid primary key,
    company_id char(7) not null,
    discrepancy_type text not null,
    source_document_id uuid not null,
    item_id uuid not null,
    warehouse_id uuid not null,
    uom_code text not null,
    status text not null,
    source_quantity numeric(18,6) not null,
    matched_quantity numeric(18,6) not null,
    remaining_quantity numeric(18,6) not null,
    summary text not null,
    latest_matched_at timestamptz null,
    updated_at timestamptz not null
  );

  alter table inventory_outbound_discrepancy_lanes
    drop constraint if exists ck_inventory_outbound_discrepancy_lanes_type;

  alter table inventory_outbound_discrepancy_lanes
    add constraint ck_inventory_outbound_discrepancy_lanes_type
      check (discrepancy_type in ('invoice_shipment', 'shipment_issue', 'invoice_coverage'));

  create unique index if not exists ux_inventory_outbound_discrepancy_lanes_natural
    on inventory_outbound_discrepancy_lanes (company_id, discrepancy_type, source_document_id, item_id, warehouse_id, uom_code);
end $$;
