-- Merge the standalone tasks_company_sequence counter into the unified
-- company_numbering_sequences table so the Task entity surfaces in
-- the per-company Numbering settings page alongside Invoice / Bill /
-- Journal Entry / etc.
--
-- Idempotent: ON CONFLICT DO NOTHING leaves an already-configured
-- task-display row untouched (e.g. an operator who pre-seeded the
-- prefix to "TASK-" via the UI before this rewire shipped wins).
-- Drops nothing — tasks_company_sequence stays as cold storage in
-- case we need to audit the legacy counter.
--
-- DEPLOYMENT NOTE: citus_app lacks DDL privileges. Apply with postgres
-- superuser BEFORE restarting accounting-api on the new build.

insert into company_numbering_sequences
    (company_id, scope_key, prefix, next_number, padding, suggestion_enabled, updated_at)
select
    s.company_id,
    'task-display',
    'TSK-',
    s.next_ordinal,
    6::smallint,
    true,
    now()
from tasks_company_sequence s
on conflict (company_id, scope_key) do nothing;
