-- =====================================================================
-- H5: drop tasks.total_direct_cost — dead column
-- =====================================================================
--
-- The column was added at task-module creation with a placeholder
-- "AP will write into this later" intention. AP never did. Every task
-- read after invoice/bill activity has the column reporting 0
-- regardless of how much AP cost the operator has accumulated against
-- the task. Per the AUDIT_2026-05-20 H5 finding the choice is:
--   (a) drop the column and the TaskRecord field, OR
--   (b) wire an AP-side write path.
--
-- This migration takes path (a) because the margin report
-- (PostgreSqlTaskMarginReportService) ALREADY computes
-- TotalDirectCost (and the base-currency variant) live from
-- bill_lines + expense_lines joined to the task via task_id. That
-- live read is the truth and was already the source for the UI
-- margin surface. Removing the dead column eliminates the "header
-- shows 0, margin report shows accurate amount" discrepancy.
--
-- If a future need surfaces for a header-attached running total
-- (e.g. to avoid the join cost on task-list cards), the right
-- mechanism is a write-through coordinator triggered by bill/expense
-- post + reverse — that's larger work than this column.
--
-- Idempotent: DROP COLUMN IF EXISTS.
-- =====================================================================

alter table tasks
  drop column if exists total_direct_cost;

-- VERSION bumped by pre-push hook.
