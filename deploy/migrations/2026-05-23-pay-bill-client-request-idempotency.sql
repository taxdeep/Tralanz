-- Pay bill draft request-intent idempotency.
-- Prevents network/UI retries from creating duplicate AP payment drafts.

ALTER TABLE pay_bills
  ADD COLUMN IF NOT EXISTS client_request_id uuid NULL,
  ADD COLUMN IF NOT EXISTS client_request_hash text NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_pay_bills_company_client_request
  ON pay_bills (company_id, client_request_id)
  WHERE client_request_id IS NOT NULL;
