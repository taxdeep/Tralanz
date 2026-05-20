-- =====================================================================
-- PR-4C: Register two missing high-risk permission tokens
-- =====================================================================
--
-- PR-4A seeded permission_registry from the existing
-- CompanyMembershipPermissionCatalog. PR-4C extends that catalog with
-- two tokens that were missing — both credit-note and vendor-credit
-- had `.view` and `.create` but no `.post`. Without these the new
-- high-risk endpoint gates have nothing to bind to.
--
-- These are high-risk (post → GL) and assignable (Owner can grant).
-- =====================================================================

begin;

insert into permission_registry
  (permission_token, module_key, group_key, action_key, description,
   is_high_risk, is_assignable)
values
  ('ar.creditnote.post', 'ar', 'creditnote', 'post',
   'Post AR credit notes to the ledger.', true, true),
  ('ap.vendorcredit.post', 'ap', 'vendorcredit', 'post',
   'Post vendor credits to the ledger.', true, true)
on conflict (permission_token) do update set
  is_high_risk = excluded.is_high_risk,
  is_assignable = excluded.is_assignable,
  module_key = excluded.module_key,
  group_key = excluded.group_key,
  action_key = excluded.action_key,
  description = excluded.description,
  updated_at = now();

commit;
