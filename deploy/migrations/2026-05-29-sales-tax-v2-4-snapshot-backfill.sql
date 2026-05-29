-- Sales Tax v2 — backfill snapshots from historic posted document lines.
--
-- For each of the six tax-bearing line tables, generates one
-- document_line_sales_tax_snapshots row per line where tax_code_id IS
-- NOT NULL AND tax_amount > 0. The legacy rate is reconstructed from
-- tax_amount / line_amount × 100 because the original schema never
-- snapshotted the rate — only the amount.
--
-- All migrated snapshots are 'primary' leg, sequence=1, treatment
-- 'taxable' (legacy schema has no treatment column; zero-rated / exempt
-- lines wouldn't have been entered as tax_code_id either way).
-- Purchase-side recoverable / non-recoverable split mirrors the migrated
-- component's recoverability_mode (default 'full').
--
-- Idempotent — ON CONFLICT (document_type, document_id, line_id,
-- sequence, leg) skip. Safe to re-run.
--
-- DEPLOYMENT NOTE: requires the data-migrate migration to have applied
-- (sales_tax_codes rows must exist with legacy_tax_code_id back-links).
-- Apply with postgres superuser:
--
--     psql -U postgres -d citus_accounting \
--          -f deploy/migrations/2026-05-29-sales-tax-v2-4-snapshot-backfill.sql

-- ========================================================================
-- 1. Common-table-expression helper: legacy → v2 component resolver
-- ========================================================================
-- Every backfill query joins through legacy_tax_code_id to find the v2
-- code + its single component + the component's jurisdiction. Pulled
-- out as a view so all six backfill statements share the join.

create or replace view _legacy_to_v2_component as
select
    stc.legacy_tax_code_id            as legacy_id,
    stc.id                            as v2_code_id,
    stc.company_id                    as company_id,
    stc.code                          as code,
    stc.name                          as name,
    stc.treatment                     as treatment,
    c.id                              as component_id,
    c.jurisdiction_id                 as jurisdiction_id,
    c.recoverability_mode             as recoverability_mode,
    coalesce(c.recoverable_percent, 100) as recoverable_percent,
    j.regime_type                     as regime_type,
    (
        select array_agg(distinct b.box_code order by b.box_code)
        from sales_tax_code_component_box_mappings m
        join sales_tax_reporting_boxes b on b.id = m.box_id
        where m.component_id = c.id
    )                                 as box_codes
from sales_tax_codes stc
join sales_tax_code_components c on c.tax_code_id = stc.id
join sales_tax_jurisdictions j   on j.id = c.jurisdiction_id
where stc.legacy_tax_code_id is not null;

-- ========================================================================
-- 2. Backfill: invoice_lines (sales side)
-- ========================================================================

insert into document_line_sales_tax_snapshots
    (company_id, document_type, document_id, line_id, sequence, leg,
     tax_code_id, component_id, jurisdiction_id,
     code_snapshot, name_snapshot, regime_type_snapshot, treatment_snapshot,
     rate_percent_snapshot, is_compound_snapshot, reporting_box_codes,
     taxable_amount, tax_amount, recoverable_amount, non_recoverable_amount,
     document_currency_code, tax_amount_base, fx_rate_snapshot, computed_at)
select
    inv.company_id,
    'invoice',
    inv.id,
    il.id,
    1,
    'primary',
    v.v2_code_id, v.component_id, v.jurisdiction_id,
    v.code, v.name, v.regime_type, v.treatment,
    case when il.line_amount > 0 then round((il.tax_amount / il.line_amount) * 100, 6) else 0 end,
    false,
    coalesce(v.box_codes, '{}'::text[]),
    il.line_amount,
    il.tax_amount,
    0,
    0,
    coalesce(inv.document_currency_code, 'CAD'),
    il.tax_amount,
    1,
    coalesce(inv.created_at, now())
from invoice_lines il
join invoices inv on inv.id = il.invoice_id
join _legacy_to_v2_component v on v.legacy_id = il.tax_code_id
where il.tax_code_id is not null
  and il.tax_amount > 0
on conflict on constraint uq_document_line_sales_tax_snapshots_natural_key do nothing;

-- ========================================================================
-- 3. Backfill: credit_note_lines (sales-side reversal)
-- ========================================================================

insert into document_line_sales_tax_snapshots
    (company_id, document_type, document_id, line_id, sequence, leg,
     tax_code_id, component_id, jurisdiction_id,
     code_snapshot, name_snapshot, regime_type_snapshot, treatment_snapshot,
     rate_percent_snapshot, is_compound_snapshot, reporting_box_codes,
     taxable_amount, tax_amount, recoverable_amount, non_recoverable_amount,
     document_currency_code, tax_amount_base, fx_rate_snapshot, computed_at)
select
    cn.company_id,
    'credit_note',
    cn.id,
    cnl.id,
    1, 'primary',
    v.v2_code_id, v.component_id, v.jurisdiction_id,
    v.code, v.name, v.regime_type, v.treatment,
    case when cnl.line_amount <> 0 then round((cnl.tax_amount / cnl.line_amount) * 100, 6) else 0 end,
    false,
    coalesce(v.box_codes, '{}'::text[]),
    cnl.line_amount,
    cnl.tax_amount,
    0,
    0,
    coalesce(cn.document_currency_code, 'CAD'),
    cnl.tax_amount,
    1,
    coalesce(cn.created_at, now())
from credit_note_lines cnl
join credit_notes cn on cn.id = cnl.credit_note_id
join _legacy_to_v2_component v on v.legacy_id = cnl.tax_code_id
where cnl.tax_code_id is not null
  and cnl.tax_amount <> 0
on conflict on constraint uq_document_line_sales_tax_snapshots_natural_key do nothing;

-- ========================================================================
-- 4. Backfill: sales_receipt_lines (cash sale)
-- ========================================================================

insert into document_line_sales_tax_snapshots
    (company_id, document_type, document_id, line_id, sequence, leg,
     tax_code_id, component_id, jurisdiction_id,
     code_snapshot, name_snapshot, regime_type_snapshot, treatment_snapshot,
     rate_percent_snapshot, is_compound_snapshot, reporting_box_codes,
     taxable_amount, tax_amount, recoverable_amount, non_recoverable_amount,
     document_currency_code, tax_amount_base, fx_rate_snapshot, computed_at)
select
    sr.company_id,
    'sales_receipt',
    sr.id,
    srl.id,
    1, 'primary',
    v.v2_code_id, v.component_id, v.jurisdiction_id,
    v.code, v.name, v.regime_type, v.treatment,
    case when srl.line_amount > 0 then round((srl.tax_amount / srl.line_amount) * 100, 6) else 0 end,
    false,
    coalesce(v.box_codes, '{}'::text[]),
    srl.line_amount,
    srl.tax_amount,
    0,
    0,
    coalesce(sr.document_currency_code, 'CAD'),
    srl.tax_amount,
    1,
    coalesce(sr.created_at, now())
from sales_receipt_lines srl
join sales_receipts sr on sr.id = srl.sales_receipt_id
join _legacy_to_v2_component v on v.legacy_id = srl.tax_code_id
where srl.tax_code_id is not null
  and srl.tax_amount > 0
on conflict on constraint uq_document_line_sales_tax_snapshots_natural_key do nothing;

-- ========================================================================
-- 5. Backfill: refund_receipt_lines (cash refund)
-- ========================================================================

insert into document_line_sales_tax_snapshots
    (company_id, document_type, document_id, line_id, sequence, leg,
     tax_code_id, component_id, jurisdiction_id,
     code_snapshot, name_snapshot, regime_type_snapshot, treatment_snapshot,
     rate_percent_snapshot, is_compound_snapshot, reporting_box_codes,
     taxable_amount, tax_amount, recoverable_amount, non_recoverable_amount,
     document_currency_code, tax_amount_base, fx_rate_snapshot, computed_at)
select
    rr.company_id,
    'refund_receipt',
    rr.id,
    rrl.id,
    1, 'primary',
    v.v2_code_id, v.component_id, v.jurisdiction_id,
    v.code, v.name, v.regime_type, v.treatment,
    case when rrl.line_amount <> 0 then round((rrl.tax_amount / rrl.line_amount) * 100, 6) else 0 end,
    false,
    coalesce(v.box_codes, '{}'::text[]),
    rrl.line_amount,
    rrl.tax_amount,
    0,
    0,
    coalesce(rr.document_currency_code, 'CAD'),
    rrl.tax_amount,
    1,
    coalesce(rr.created_at, now())
from refund_receipt_lines rrl
join refund_receipts rr on rr.id = rrl.refund_receipt_id
join _legacy_to_v2_component v on v.legacy_id = rrl.tax_code_id
where rrl.tax_code_id is not null
  and rrl.tax_amount <> 0
on conflict on constraint uq_document_line_sales_tax_snapshots_natural_key do nothing;

-- ========================================================================
-- 6. Backfill: bill_lines (purchase side)
-- ========================================================================
-- Purchase side splits tax_amount into recoverable + non_recoverable
-- using the migrated component's recoverability_mode. Legacy
-- bill_lines.is_tax_recoverable column overrides when present:
--   * true  → full recoverable
--   * false → full non-recoverable

insert into document_line_sales_tax_snapshots
    (company_id, document_type, document_id, line_id, sequence, leg,
     tax_code_id, component_id, jurisdiction_id,
     code_snapshot, name_snapshot, regime_type_snapshot, treatment_snapshot,
     rate_percent_snapshot, is_compound_snapshot, reporting_box_codes,
     taxable_amount, tax_amount, recoverable_amount, non_recoverable_amount,
     document_currency_code, tax_amount_base, fx_rate_snapshot, computed_at)
select
    b.company_id,
    'bill',
    b.id,
    bl.id,
    1, 'primary',
    v.v2_code_id, v.component_id, v.jurisdiction_id,
    v.code, v.name, v.regime_type, v.treatment,
    case when bl.line_amount > 0 then round((bl.tax_amount / bl.line_amount) * 100, 6) else 0 end,
    false,
    coalesce(v.box_codes, '{}'::text[]),
    bl.line_amount,
    bl.tax_amount,
    -- recoverable_amount
    case
        when bl.is_tax_recoverable is not null and bl.is_tax_recoverable = false then 0
        when v.recoverability_mode = 'none'    then 0
        when v.recoverability_mode = 'partial' then round(bl.tax_amount * v.recoverable_percent / 100, 6)
        else bl.tax_amount  -- full
    end,
    -- non_recoverable_amount
    case
        when bl.is_tax_recoverable is not null and bl.is_tax_recoverable = false then bl.tax_amount
        when v.recoverability_mode = 'none'    then bl.tax_amount
        when v.recoverability_mode = 'partial' then round(bl.tax_amount * (100 - v.recoverable_percent) / 100, 6)
        else 0  -- full
    end,
    coalesce(b.document_currency_code, 'CAD'),
    bl.tax_amount,
    1,
    coalesce(b.created_at, now())
from bill_lines bl
join bills b on b.id = bl.bill_id
join _legacy_to_v2_component v on v.legacy_id = bl.tax_code_id
where bl.tax_code_id is not null
  and bl.tax_amount > 0
on conflict on constraint uq_document_line_sales_tax_snapshots_natural_key do nothing;

-- ========================================================================
-- 7. Backfill: vendor_credit_lines (purchase-side reversal)
-- ========================================================================

insert into document_line_sales_tax_snapshots
    (company_id, document_type, document_id, line_id, sequence, leg,
     tax_code_id, component_id, jurisdiction_id,
     code_snapshot, name_snapshot, regime_type_snapshot, treatment_snapshot,
     rate_percent_snapshot, is_compound_snapshot, reporting_box_codes,
     taxable_amount, tax_amount, recoverable_amount, non_recoverable_amount,
     document_currency_code, tax_amount_base, fx_rate_snapshot, computed_at)
select
    vc.company_id,
    'vendor_credit',
    vc.id,
    vcl.id,
    1, 'primary',
    v.v2_code_id, v.component_id, v.jurisdiction_id,
    v.code, v.name, v.regime_type, v.treatment,
    case when vcl.line_amount <> 0 then round((vcl.tax_amount / vcl.line_amount) * 100, 6) else 0 end,
    false,
    coalesce(v.box_codes, '{}'::text[]),
    vcl.line_amount,
    vcl.tax_amount,
    case
        when vcl.is_tax_recoverable is not null and vcl.is_tax_recoverable = false then 0
        when v.recoverability_mode = 'none'    then 0
        when v.recoverability_mode = 'partial' then round(vcl.tax_amount * v.recoverable_percent / 100, 6)
        else vcl.tax_amount
    end,
    case
        when vcl.is_tax_recoverable is not null and vcl.is_tax_recoverable = false then vcl.tax_amount
        when v.recoverability_mode = 'none'    then vcl.tax_amount
        when v.recoverability_mode = 'partial' then round(vcl.tax_amount * (100 - v.recoverable_percent) / 100, 6)
        else 0
    end,
    coalesce(vc.document_currency_code, 'CAD'),
    vcl.tax_amount,
    1,
    coalesce(vc.created_at, now())
from vendor_credit_lines vcl
join vendor_credits vc on vc.id = vcl.vendor_credit_id
join _legacy_to_v2_component v on v.legacy_id = vcl.tax_code_id
where vcl.tax_code_id is not null
  and vcl.tax_amount <> 0
on conflict on constraint uq_document_line_sales_tax_snapshots_natural_key do nothing;

-- ========================================================================
-- 8. Verification
-- ========================================================================
-- Per document_type, the count of backfilled snapshots should equal
-- the count of legacy tax-bearing lines for that document type.

do $$
declare
    src_invoice_lines        int; tgt_invoice            int;
    src_credit_note_lines    int; tgt_credit_note        int;
    src_sales_receipt_lines  int; tgt_sales_receipt      int;
    src_refund_receipt_lines int; tgt_refund_receipt     int;
    src_bill_lines           int; tgt_bill               int;
    src_vendor_credit_lines  int; tgt_vendor_credit      int;
    sum_src_tax              numeric;
    sum_tgt_tax              numeric;
begin
    select count(*) into src_invoice_lines from invoice_lines il
        join _legacy_to_v2_component v on v.legacy_id = il.tax_code_id
        where il.tax_code_id is not null and il.tax_amount > 0;
    select count(*) into tgt_invoice from document_line_sales_tax_snapshots where document_type = 'invoice';

    select count(*) into src_credit_note_lines from credit_note_lines cnl
        join _legacy_to_v2_component v on v.legacy_id = cnl.tax_code_id
        where cnl.tax_code_id is not null and cnl.tax_amount <> 0;
    select count(*) into tgt_credit_note from document_line_sales_tax_snapshots where document_type = 'credit_note';

    select count(*) into src_sales_receipt_lines from sales_receipt_lines srl
        join _legacy_to_v2_component v on v.legacy_id = srl.tax_code_id
        where srl.tax_code_id is not null and srl.tax_amount > 0;
    select count(*) into tgt_sales_receipt from document_line_sales_tax_snapshots where document_type = 'sales_receipt';

    select count(*) into src_refund_receipt_lines from refund_receipt_lines rrl
        join _legacy_to_v2_component v on v.legacy_id = rrl.tax_code_id
        where rrl.tax_code_id is not null and rrl.tax_amount <> 0;
    select count(*) into tgt_refund_receipt from document_line_sales_tax_snapshots where document_type = 'refund_receipt';

    select count(*) into src_bill_lines from bill_lines bl
        join _legacy_to_v2_component v on v.legacy_id = bl.tax_code_id
        where bl.tax_code_id is not null and bl.tax_amount > 0;
    select count(*) into tgt_bill from document_line_sales_tax_snapshots where document_type = 'bill';

    select count(*) into src_vendor_credit_lines from vendor_credit_lines vcl
        join _legacy_to_v2_component v on v.legacy_id = vcl.tax_code_id
        where vcl.tax_code_id is not null and vcl.tax_amount <> 0;
    select count(*) into tgt_vendor_credit from document_line_sales_tax_snapshots where document_type = 'vendor_credit';

    raise notice 'Sales Tax v2 snapshot backfill counts (source / target):';
    raise notice '  invoice         : % / %', src_invoice_lines,        tgt_invoice;
    raise notice '  credit_note     : % / %', src_credit_note_lines,    tgt_credit_note;
    raise notice '  sales_receipt   : % / %', src_sales_receipt_lines,  tgt_sales_receipt;
    raise notice '  refund_receipt  : % / %', src_refund_receipt_lines, tgt_refund_receipt;
    raise notice '  bill            : % / %', src_bill_lines,           tgt_bill;
    raise notice '  vendor_credit   : % / %', src_vendor_credit_lines,  tgt_vendor_credit;

    -- Tax-amount totals reconciliation. Snapshot table sums in document
    -- currency (we treat each as base for the initial backfill since
    -- legacy *_lines.tax_amount was already in doc currency).
    select coalesce(sum(il.tax_amount), 0) into sum_src_tax from invoice_lines il
        join _legacy_to_v2_component v on v.legacy_id = il.tax_code_id
        where il.tax_code_id is not null and il.tax_amount > 0;
    select coalesce(sum(tax_amount), 0) into sum_tgt_tax from document_line_sales_tax_snapshots
        where document_type = 'invoice';
    raise notice '  Σ invoice tax (src / tgt) : % / %', sum_src_tax, sum_tgt_tax;

    if tgt_invoice <> src_invoice_lines
       or tgt_credit_note <> src_credit_note_lines
       or tgt_sales_receipt <> src_sales_receipt_lines
       or tgt_refund_receipt <> src_refund_receipt_lines
       or tgt_bill <> src_bill_lines
       or tgt_vendor_credit <> src_vendor_credit_lines then
        raise notice 'WARNING: source vs target row count mismatch on at least one document type. Investigate before relying on the snapshot table.';
    end if;
end$$;

-- ========================================================================
-- 9. Cleanup helper view
-- ========================================================================
-- The view exists only for this backfill; subsequent migrations don't
-- depend on it. Dropping at the end keeps the schema tidy.

drop view if exists _legacy_to_v2_component;
