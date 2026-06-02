-- Preserve exact task-line provenance on AR invoice lines.
-- This lets a draft invoice be saved first and posted later without losing
-- the line-level task billing link.

ALTER TABLE invoice_lines
  ADD COLUMN IF NOT EXISTS task_id uuid NULL,
  ADD COLUMN IF NOT EXISTS task_line_id uuid NULL;

DO $$
BEGIN
  IF to_regclass('public.task_lines') IS NOT NULL THEN
    UPDATE invoice_lines il
       SET task_line_id = tl.id
      FROM task_lines tl
     WHERE il.task_line_id IS NULL
       AND il.task_id IS NOT NULL
       AND tl.company_id = il.company_id
       AND tl.task_id = il.task_id
       AND tl.line_no = il.line_number;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_invoice_lines_task_line
  ON invoice_lines (company_id, task_id, task_line_id)
  WHERE task_id IS NOT NULL;
