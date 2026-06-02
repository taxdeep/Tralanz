-- R4 (purchase): carry the selected Tax Code (tax_code_sets bundle) on bill
-- lines, mirroring invoice_lines / expense_lines. Nullable; the sales-tax
-- engine reads it to compute the multi-rule purchase tax (ITC split).
alter table bill_lines add column if not exists tax_code_set_id uuid;
