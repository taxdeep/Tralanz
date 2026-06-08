-- R4 (sales, estimate-only): carry the selected Tax Code (tax_code_sets bundle)
-- on quote + sales-order lines, parallel to the legacy tax_code_id (Tax Rule),
-- mirroring invoice_lines / bill_lines / expense_lines. Nullable, no FK.
-- Quotes/SOs do not post tax to the ledger; the set id is stored, displayed,
-- and carried forward to the invoice on quote->sales-order conversion.
alter table quote_lines       add column if not exists tax_code_set_id uuid;
alter table sales_order_lines add column if not exists tax_code_set_id uuid;
