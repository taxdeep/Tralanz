#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# Tralanz SysAdmin lockout escape hatch.
#
# When the *only* SysAdmin account locks itself out (5-fail temporary lock or
# 3-temp-lock-in-36h permanent ban), no one can log into the SysAdmin shell
# to lift it via the Locked Accounts page. Run this script as root on the
# server to:
#
#   1. mark every active lockout (temporary + permanent) for the given
#      email as lifted, with reason "emergency CLI"
#   2. flip the matching sysadmin_accounts row's status back to 'active'
#
# Usage:
#   sudo ./lift-sysadmin-lockout.sh user@example.com
#   sudo ./lift-sysadmin-lockout.sh --realm business user@example.com
#
# Defaults to the sysadmin realm. Reads PG credentials from /etc/citus/citus.env.
# -----------------------------------------------------------------------------
set -Eeuo pipefail

readonly ENV_FILE="/etc/citus/citus.env"

usage() {
  cat <<'EOF'
Usage:
  sudo ./lift-sysadmin-lockout.sh [--realm sysadmin|business] EMAIL

Examples:
  sudo ./lift-sysadmin-lockout.sh ops@yourcompany.com
  sudo ./lift-sysadmin-lockout.sh --realm business jane@yourcompany.com
EOF
}

if [[ "${EUID}" -ne 0 ]]; then
  echo "ERROR: run as root or via sudo." >&2
  exit 1
fi

if [[ ! -f "${ENV_FILE}" ]]; then
  echo "ERROR: ${ENV_FILE} not found. Is Tralanz installed on this host?" >&2
  exit 1
fi

realm="sysadmin"
email=""
while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --realm)
      [[ "$#" -ge 2 ]] || { echo "ERROR: --realm needs a value." >&2; exit 1; }
      realm="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    -*)
      echo "ERROR: unknown flag $1" >&2
      usage
      exit 1
      ;;
    *)
      if [[ -n "${email}" ]]; then
        echo "ERROR: multiple emails provided." >&2
        exit 1
      fi
      email="$1"
      shift
      ;;
  esac
done

if [[ -z "${email}" ]]; then
  echo "ERROR: email argument is required." >&2
  usage
  exit 1
fi

case "${realm}" in
  sysadmin|business) ;;
  *)
    echo "ERROR: --realm must be 'sysadmin' or 'business' (got '${realm}')." >&2
    exit 1
    ;;
esac

# Load DB connection details from citus.env (same env file the systemd
# units source). We only need CITUS_DB_HOST / PORT / NAME / USER / PASSWORD.
# shellcheck disable=SC1090
set -a; . "${ENV_FILE}"; set +a

: "${CITUS_DB_HOST:?Missing CITUS_DB_HOST in ${ENV_FILE}}"
: "${CITUS_DB_PORT:?Missing CITUS_DB_PORT}"
: "${CITUS_DB_NAME:?Missing CITUS_DB_NAME}"
: "${CITUS_DB_USER:?Missing CITUS_DB_USER}"
: "${CITUS_DB_PASSWORD:?Missing CITUS_DB_PASSWORD}"

normalized_email="$(printf '%s' "${email}" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
email_hash="$(printf '%s' "${normalized_email}" | sha256sum | awk '{ print toupper($1) }')"

readonly account_table="$( [[ "${realm}" == "sysadmin" ]] && echo "sysadmin_accounts" || echo "users" )"

echo "Lifting active ${realm} lockouts for ${normalized_email}..."

PGPASSWORD="${CITUS_DB_PASSWORD}" psql \
  -h "${CITUS_DB_HOST}" \
  -p "${CITUS_DB_PORT}" \
  -U "${CITUS_DB_USER}" \
  -d "${CITUS_DB_NAME}" \
  -v ON_ERROR_STOP=1 \
  -X -q \
  <<SQL
begin;

with lifted as (
  update account_lockouts
     set lifted_at = now(),
         lifted_reason = 'emergency CLI: lift-sysadmin-lockout.sh'
   where realm = '${realm}'
     and email_hash = '${email_hash}'
     and lifted_at is null
   returning id, account_id, lockout_kind
)
update ${account_table}
   set status = 'active', updated_at = now()
 where id in (select account_id from lifted where account_id is not null)
   and status = 'locked';

commit;

select count(*)::text || ' active lockouts cleared.' as result
  from account_lockouts
 where realm = '${realm}'
   and email_hash = '${email_hash}'
   and lifted_at >= now() - interval '5 seconds';
SQL

echo "Done. ${normalized_email} can sign in again on next attempt."
