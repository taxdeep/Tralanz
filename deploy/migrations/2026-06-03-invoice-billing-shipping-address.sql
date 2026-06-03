-- Invoice billing / shipping address (free-text), surfaced on the New
-- invoice Header. Nullable; the UI pre-fills them from the selected
-- customer's address and the operator can edit per invoice. Idempotent.
alter table invoices add column if not exists billing_address text null;
alter table invoices add column if not exists shipping_address text null;
