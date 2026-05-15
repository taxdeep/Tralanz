-- Move journal-entry line audit metadata out of runtime posting/review paths.
-- Production app roles must not need DDL privileges for posting, voiding, or reviewing journals.

ALTER TABLE journal_entry_lines
  ADD COLUMN IF NOT EXISTS posting_role text NULL,
  ADD COLUMN IF NOT EXISTS source_line_number integer NULL;
