-- H14: force the bootstrap SysAdmin account to rotate its seeded password on
-- first login. not-null default false => every existing row backfills to false,
-- so no current admin is ever forced. password_changed_at is informational.
alter table sysadmin_accounts
  add column if not exists must_change_password boolean not null default false;
alter table sysadmin_accounts
  add column if not exists password_changed_at timestamptz;
