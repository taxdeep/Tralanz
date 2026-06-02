-- Receive payment draft request-intent idempotency.
-- Prevents network/UI retries from creating duplicate AR receipt drafts.

ALTER TABLE receive_payments
  ADD COLUMN IF NOT EXISTS client_request_id uuid NULL,
  ADD COLUMN IF NOT EXISTS client_request_hash text NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_receive_payments_company_client_request
  ON receive_payments (company_id, client_request_id)
  WHERE client_request_id IS NOT NULL;
