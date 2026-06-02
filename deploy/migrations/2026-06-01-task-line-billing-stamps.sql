-- Task line billing stamps.
-- Allows invoice / sales receipt posting follow-up flows to mark source task
-- lines as billed without relying on runtime schema bootstrap only.

ALTER TABLE task_lines
  ADD COLUMN IF NOT EXISTS billed_source_type varchar(32) NULL,
  ADD COLUMN IF NOT EXISTS billed_source_id uuid NULL,
  ADD COLUMN IF NOT EXISTS billed_source_line_id uuid NULL,
  ADD COLUMN IF NOT EXISTS billed_at timestamptz NULL;

CREATE INDEX IF NOT EXISTS ix_task_lines_billing_state
  ON task_lines (company_id, task_id)
  WHERE billed_source_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_task_lines_billing_source
  ON task_lines (company_id, billed_source_type, billed_source_id)
  WHERE billed_source_id IS NOT NULL;
