-- Tomb-stone test for the migration-runner refactor (run as
-- postgres superuser via peer auth, see common.sh /
-- apply_pending_migrations()).
--
-- Adds an unused comment to a postgres-owned table. Idempotent —
-- COMMENT ON re-applies the same string with no error. The point
-- is to exercise the "ALTER something owned by postgres" path that
-- previously hit 42501 when migrations ran as citus_app.
--
-- Safe to leave in place. The comment is meta-data only and
-- doesn't affect any query plan or runtime behaviour.

comment on table schema_migrations is
  'Deploy-time tracker: each row = one deploy/migrations/*.sql file already applied. Names sort chronologically by their YYYY-MM-DD prefix.';
