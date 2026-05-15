-- Bank reconciliation ledger lock/snapshot tables.
-- Apply with an owner/migration role before deploying the application role.

CREATE TABLE IF NOT EXISTS bank_reconciliations (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id char(7) NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  bank_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  statement_date date NOT NULL,
  opening_balance numeric(20,6) NOT NULL,
  ending_balance numeric(20,6) NOT NULL,
  cleared_increase numeric(20,6) NOT NULL,
  cleared_decrease numeric(20,6) NOT NULL,
  calculated_ending_balance numeric(20,6) NOT NULL,
  difference numeric(20,6) NOT NULL,
  status text NOT NULL,
  line_count integer NOT NULL,
  notes text,
  completed_by_user_id char(7) NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  completed_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT bank_reconciliations_status_chk CHECK (status IN ('completed')),
  CONSTRAINT bank_reconciliations_line_count_chk CHECK (line_count > 0),
  CONSTRAINT bank_reconciliations_zero_difference_chk CHECK (abs(difference) < 0.005)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_bank_reconciliations_statement
  ON bank_reconciliations (company_id, bank_account_id, statement_date)
  WHERE status = 'completed';

CREATE INDEX IF NOT EXISTS ix_bank_reconciliations_company_account_date
  ON bank_reconciliations (company_id, bank_account_id, statement_date DESC);

CREATE TABLE IF NOT EXISTS bank_reconciliation_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  reconciliation_id uuid NOT NULL REFERENCES bank_reconciliations(id) ON DELETE CASCADE,
  company_id char(7) NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  ledger_entry_id uuid NOT NULL REFERENCES ledger_entries(id) ON DELETE RESTRICT,
  journal_entry_id uuid NOT NULL REFERENCES journal_entries(id) ON DELETE RESTRICT,
  journal_entry_line_id uuid NOT NULL REFERENCES journal_entry_lines(id) ON DELETE RESTRICT,
  posting_date date NOT NULL,
  transaction_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  tx_debit numeric(20,6) NOT NULL,
  tx_credit numeric(20,6) NOT NULL,
  debit numeric(20,6) NOT NULL,
  credit numeric(20,6) NOT NULL,
  signed_amount_base numeric(20,6) NOT NULL,
  signed_amount_transaction numeric(20,6) NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT bank_reconciliation_lines_nonnegative_chk CHECK (
    tx_debit >= 0 AND tx_credit >= 0 AND debit >= 0 AND credit >= 0
  )
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_bank_reconciliation_lines_ledger_entry
  ON bank_reconciliation_lines (ledger_entry_id);

CREATE INDEX IF NOT EXISTS ix_bank_reconciliation_lines_reconciliation
  ON bank_reconciliation_lines (reconciliation_id);

CREATE INDEX IF NOT EXISTS ix_bank_reconciliation_lines_company_posting_date
  ON bank_reconciliation_lines (company_id, posting_date DESC);
