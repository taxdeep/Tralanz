-- Receive payment overpayment parking as Customer Deposit.
-- Keeps cash receipt posting, customer_deposits, and AR credit open-item
-- creation idempotent per receive payment.

ALTER TABLE receive_payments
  ADD COLUMN IF NOT EXISTS extra_deposit_amount numeric(20,6) NOT NULL DEFAULT 0;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'receive_payments_extra_deposit_nonnegative_chk'
  ) THEN
    ALTER TABLE receive_payments
      ADD CONSTRAINT receive_payments_extra_deposit_nonnegative_chk
      CHECK (extra_deposit_amount >= 0);
  END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_customer_deposits_company_source_receive_payment
  ON customer_deposits (company_id, source_receive_payment_id)
  WHERE source_receive_payment_id IS NOT NULL;
