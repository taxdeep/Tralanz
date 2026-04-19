#!/bin/sh
set -eu

if [ "${CITUS_ENABLE_DEMO_SEED:-true}" != "true" ]; then
  echo "Citus demo seed is disabled."
  exit 0
fi

echo "Applying Citus demo business seed."

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
insert into companies (
  id,
  entity_number,
  legal_name,
  base_currency_code,
  multi_currency_enabled,
  status
)
values
  (
    '5e492df2-37ab-47df-a1bb-2d559c876cbc',
    'EN202600000001',
    'Northwind Studio Ltd.',
    'USD',
    true,
    'active'
  ),
  (
    'e56df08c-39ae-405b-8ed2-247b97d2f9f6',
    'EN202600000002',
    'Blue Harbor Trading Co.',
    'CAD',
    false,
    'active'
  )
on conflict (id) do update
set entity_number = excluded.entity_number,
    legal_name = excluded.legal_name,
    base_currency_code = excluded.base_currency_code,
    multi_currency_enabled = excluded.multi_currency_enabled,
    status = excluded.status;

insert into users (
  id,
  email,
  username,
  display_name,
  password_hash,
  status,
  email_verified_at,
  mfa_mode
)
values
  (
    '7bd0e908-cfe7-4f7b-8a0d-f19292e4186d',
    'alice.rowan@northwind.example',
    'alice.rowan',
    'Alice Rowan',
    'DemoPass123!',
    'active',
    now(),
    'none'
  ),
  (
    '3512739f-2af3-41f5-8fd4-d648d913a274',
    'ben.mercer@blueharbor.example',
    'ben.mercer',
    'Ben Mercer',
    'DemoPass123!',
    'active',
    now(),
    'none'
  )
on conflict (id) do update
set email = excluded.email,
    username = excluded.username,
    display_name = excluded.display_name,
    password_hash = excluded.password_hash,
    status = excluded.status,
    email_verified_at = excluded.email_verified_at,
    mfa_mode = excluded.mfa_mode;

insert into company_memberships (
  id,
  company_id,
  user_id,
  role,
  is_active,
  permissions
)
values
  (
    '7f9db7cf-9eb8-4fe7-b8aa-4b6ea51b20a1',
    '5e492df2-37ab-47df-a1bb-2d559c876cbc',
    '7bd0e908-cfe7-4f7b-8a0d-f19292e4186d',
    'owner',
    true,
    '["reports"]'::jsonb
  ),
  (
    'ac2bd623-64e3-41ca-b2a2-fa6511962711',
    'e56df08c-39ae-405b-8ed2-247b97d2f9f6',
    '3512739f-2af3-41f5-8fd4-d648d913a274',
    'user',
    true,
    '["ap"]'::jsonb
  )
on conflict (id) do update
set company_id = excluded.company_id,
    user_id = excluded.user_id,
    role = excluded.role,
    is_active = excluded.is_active,
    permissions = excluded.permissions;
SQL
