-- Keep business session revocation schema in migration-managed PostgreSQL.
-- Password reset marks active sessions revoked instead of physically deleting
-- them, and session validation must ignore revoked rows.

alter table business_sessions
  add column if not exists revoked_at timestamptz;

create index if not exists idx_business_sessions_active_user_company_expiry
  on business_sessions (user_id, active_company_id, expires_at desc)
  where revoked_at is null;

create index if not exists idx_business_sessions_active_token_expiry
  on business_sessions (token_hash, expires_at desc)
  where revoked_at is null;
