#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# Citus reset — wipes the Accounting database back to "first run" state and
# restarts every Citus service. After this completes, the deploy looks exactly
# like a brand-new install: SysAdmin login lands on "Create first SysAdmin",
# Business login is gated behind real auth, no companies / users / accounts /
# tax codes / journal entries / inventory rows exist anywhere.
#
# What this DOES touch:
#   - Drops every table in the public schema of the Citus accounting database
#     (companies, users, accounts, journal_entries, ledger_entries, tax_codes,
#     unitysearch tables, audit, runtime state — everything).
#   - Recreates the schema empty. The Accounting + SysAdmin APIs re-EnsureSchema
#     on the next startup, restoring the platform tables + ISO 4217 currency
#     catalog.
#   - systemctl restart on the four Citus services.
#
# What this does NOT touch:
#   - Postgres role / password / pg_hba.conf — only the database content.
#   - /etc/citus/citus.env — connection string and other env config stay put.
#   - Nginx / TLS certificates.
#   - Browser sessionStorage on your own machine. The Business shell may still
#     hold a stale "bootstrap:..." token after reset; clear it manually
#     (DevTools -> Application -> Session Storage -> delete keys) or just hit
#     /sysadmin first, sign in, run First Company Wizard from a fresh tab.
#
# Usage:
#   sudo ./deploy/ubuntu24/reset.sh           # interactive, asks before wipe
#   sudo ./deploy/ubuntu24/reset.sh --yes     # non-interactive
#   sudo ./deploy/ubuntu24/reset.sh -y
#
# Exit codes:
#   0  database reset and services restarted
#   1  confirmation declined / preflight check failed
#   2  database operation failed
#   3  one or more services did not become healthy after restart
# -----------------------------------------------------------------------------
set -Eeuo pipefail

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "${SCRIPT_DIR}/common.sh"

ASSUME_YES=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes|-y)
      ASSUME_YES=1
      shift
      ;;
    -h|--help)
      cat <<'EOF'
Usage: sudo ./reset.sh [--yes|-y]

Drops the Citus accounting database back to "first run" empty state and
restarts every Citus service. Asks for confirmation unless --yes is passed.
EOF
      exit 0
      ;;
    *)
      fail "Unknown option: $1 (try --help)"
      ;;
  esac
done

if [[ "${EUID}" -ne 0 ]]; then
  fail "reset.sh must be run as root (use sudo)."
fi

if [[ ! -f "${ENV_FILE}" ]]; then
  fail "Env file ${ENV_FILE} is missing — has install.sh ever been run on this host?"
fi

# Pull CITUS_ACCOUNTING_DB out of the env file without sourcing the rest of it.
CONN_STRING="$(awk -F= '/^CITUS_ACCOUNTING_DB=/{sub(/^CITUS_ACCOUNTING_DB=/, ""); print; exit}' "${ENV_FILE}")"
if [[ -z "${CONN_STRING}" ]]; then
  fail "CITUS_ACCOUNTING_DB not found in ${ENV_FILE}."
fi

extract_field() {
  # $1 = key (e.g. Host), prints the value or empty string.
  local key="$1"
  printf '%s' "${CONN_STRING}" \
    | tr ';' '\n' \
    | awk -F= -v k="${key}" 'tolower($1) == tolower(k) { sub(/^[^=]*=/, ""); print; exit }'
}

DB_HOST="$(extract_field Host)"
DB_PORT="$(extract_field Port)"
DB_NAME="$(extract_field Database)"
DB_USER="$(extract_field Username)"
DB_PASSWORD="$(extract_field Password)"

[[ -n "${DB_HOST}"     ]] || fail "Could not parse Host from CITUS_ACCOUNTING_DB."
[[ -n "${DB_NAME}"     ]] || fail "Could not parse Database from CITUS_ACCOUNTING_DB."
[[ -n "${DB_USER}"     ]] || fail "Could not parse Username from CITUS_ACCOUNTING_DB."
[[ -n "${DB_PASSWORD}" ]] || fail "Could not parse Password from CITUS_ACCOUNTING_DB."
DB_PORT="${DB_PORT:-5432}"

cat <<EOF
====================================================================
  Citus reset — DESTRUCTIVE
--------------------------------------------------------------------
  Database host    : ${DB_HOST}:${DB_PORT}
  Database name    : ${DB_NAME}
  Database user    : ${DB_USER}
  Services restart : citus-accounting-api, citus-sysadmin-api,
                     citus-web, citus-sysadmin-web

  Every table in the '${DB_NAME}' database (public schema) will be
  DROPPED. Companies, users, accounts, journal entries, audit logs,
  reconciliations, payments, attachments, sessions — all gone.

  Postgres role / password / pg_hba.conf / nginx / TLS certs /
  /etc/citus/citus.env are NOT touched.
====================================================================
EOF

if [[ "${ASSUME_YES}" -ne 1 ]]; then
  read -r -p "Type 'reset' to confirm, anything else to abort: " CONFIRM
  if [[ "${CONFIRM}" != "reset" ]]; then
    log "Aborted — no changes made."
    exit 1
  fi
fi

log "Stopping Citus application services."
stop_application_services

# psql may live at the Postgres-version-specific path on Ubuntu (the apt
# package symlinks /usr/bin/psql to the active client). Trust PATH.
if ! command -v psql >/dev/null 2>&1; then
  fail "psql is not installed or not on PATH."
fi

run_psql() {
  # $1 = target database, $2... = SQL/options. PGPASSWORD is exported only
  # for the duration of this call. -v ON_ERROR_STOP=1 makes any failure
  # propagate out so we can short-circuit the script.
  local db="$1"; shift
  PGPASSWORD="${DB_PASSWORD}" psql \
    --host="${DB_HOST}" \
    --port="${DB_PORT}" \
    --username="${DB_USER}" \
    --dbname="${db}" \
    --no-psqlrc \
    --quiet \
    -v ON_ERROR_STOP=1 \
    "$@"
}

log "Verifying connectivity to Postgres."
if ! run_psql postgres -c 'SELECT 1;' >/dev/null 2>&1; then
  fail "Cannot connect to Postgres at ${DB_HOST}:${DB_PORT} as ${DB_USER}. Check pg_hba.conf and the credentials in ${ENV_FILE}."
fi

# DROP SCHEMA public CASCADE is the standard Postgres-native way to wipe
# everything in the database without dropping the database itself — keeps
# DB owner / role grants / extensions installed at the database level
# untouched. Then recreate an empty public schema and re-grant default privs.
log "Wiping public schema in '${DB_NAME}'."
run_psql "${DB_NAME}" <<SQL || exit 2
DROP SCHEMA IF EXISTS public CASCADE;
CREATE SCHEMA public;
GRANT ALL ON SCHEMA public TO ${DB_USER};
GRANT ALL ON SCHEMA public TO public;
COMMENT ON SCHEMA public IS 'standard public schema';
SQL

log "Public schema dropped and recreated. Database is empty."

# Restart services — each will re-EnsureSchema on startup, restoring the
# platform tables + ISO 4217 currency catalog. In Development mode the
# bootstrap fixtures (Northwind / Blue Harbor / Alice / Ben) are seeded;
# in Production they are skipped, leaving the database genuinely empty
# until the SysAdmin First-Company Wizard runs.
log "Restarting Citus services so they re-create their schema."
restart_application_services

ACCOUNTING_HEALTH_URL="${CITUS_ACCOUNTING_HEALTH_URL:-http://127.0.0.1:5088/health}"
SYSADMIN_HEALTH_URL="${CITUS_SYSADMIN_HEALTH_URL:-http://127.0.0.1:5089/health}"

OK=1
if ! wait_for_http "citus-accounting-api" "${ACCOUNTING_HEALTH_URL}"; then
  OK=0
fi
if ! wait_for_http "citus-sysadmin-api" "${SYSADMIN_HEALTH_URL}"; then
  OK=0
fi

if [[ "${OK}" -ne 1 ]]; then
  cat <<EOF >&2

WARNING: at least one API did not become healthy after restart.
Check journalctl:
    journalctl -u citus-accounting-api -n 80 --no-pager
    journalctl -u citus-sysadmin-api   -n 80 --no-pager
The schema is wiped, but the apps may need a manual restart or a code/
config fix before the deploy is usable again.
EOF
  exit 3
fi

cat <<'EOF'

====================================================================
  Reset complete.
--------------------------------------------------------------------
  Next steps:
    1. Open the SysAdmin shell in your browser (e.g. /sysadmin).
       It will show "Create first SysAdmin" — provision the operator.
    2. Sign in as that SysAdmin, run "First Company Wizard" to create
       the first real Owner + Company.
    3. Open the Business shell, sign in with the Owner credentials
       you just created. Chart of Accounts, Tax Rates, Journal Entry,
       Reports — all fresh, no demo data.

  If your browser still has a stale "bootstrap:..." session token
  for the Business shell (left over from before the reset), clear
  Session Storage for the Business domain in DevTools, then refresh.
====================================================================
EOF
