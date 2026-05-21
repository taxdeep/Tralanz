-- =====================================================================
-- Permission tokens added in P0-5a (PR #32) and P0-5c (PR #33)
-- =====================================================================
--
-- The C# catalog (CompanyMembershipPermissionCatalog) gained two
-- new high-risk tokens during the 2026-05-20 audit closure:
--
--   * gl.journal.void  (P0-5a / PR #32) — posted-JE void gate; closes
--     audit C5. Catalog entry registered, endpoint
--     POST /journal-entries/{id}/void wired with
--     .RequireGrantedPermission(...GlJournalVoid).
--   * ar.invoice.send  (P0-5c / PR #33) — invoice email send gate;
--     closes audit C10. Catalog entry registered, endpoint
--     POST /document-review/invoice/{id}/send wired with both
--     .RequireGrantedPermission(...ArInvoiceSend) AND
--     .RequireRateLimiting("invoice-send").
--
-- Both PRs added the tokens to C# but no corresponding migration was
-- shipped, so company_user_permissions.permission_token FK to
-- permission_registry blocks the dual-write sync added in P1-1.
-- Without this migration:
--   * SysAdmin Preset Apply that includes either token would FK-error
--     in PostgreSqlCompanyMembershipPermissionStore.SyncGrantTokenAsync.
--   * Business UI Grant of either token via PostgreSqlPermissionGrantStore
--     .InsertGrantAsync would FK-error at the company_user_permissions
--     insert.
-- =====================================================================

begin;

insert into permission_registry
  (permission_token, module_key, group_key, action_key, description,
   is_high_risk, is_assignable)
values
  ('gl.journal.void', 'gl', 'journal', 'void',
   'Void posted journal entries (writes compensating reversal).', true, true),
  ('ar.invoice.send', 'ar', 'invoice', 'send',
   'Send AR invoice emails to customers (with PDF attached).', true, true)
on conflict (permission_token) do update set
  is_high_risk = excluded.is_high_risk,
  is_assignable = excluded.is_assignable,
  module_key = excluded.module_key,
  group_key = excluded.group_key,
  action_key = excluded.action_key,
  description = excluded.description,
  updated_at = now();

commit;
