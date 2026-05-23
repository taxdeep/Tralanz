#!/usr/bin/env bash
# Daily PostgreSQL backup for Tralanz Books.
#
# Fired by citus-backup.timer (default: 03:00 UTC daily). Reads DB
# credentials from /etc/citus/citus.env, runs pg_dump in custom format,
# gzips the output to /opt/citus/backups, and rotates anything older
# than CITUS_BACKUP_RETENTION_DAYS (7 by default).
#
# Logging goes to the systemd journal — `journalctl -u citus-backup`
# is the audit trail. To restore, see deploy/RESTORE.md at the repo
# root.
set -euo pipefail

ENV_FILE="${CITUS_ENV_FILE:-/etc/citus/citus.env}"
BACKUP_DIR="${CITUS_BACKUP_DIR:-/opt/citus/backups}"
RETENTION_DAYS="${CITUS_BACKUP_RETENTION_DAYS:-7}"

[[ -f "$ENV_FILE" ]] || { echo "Missing $ENV_FILE — run install.sh first." >&2; exit 1; }

# Source env without leaking secrets through `printenv`. The Postgres
# credentials live there as KEY=value lines; `set -a` exports them for
# pg_dump's environment, `set +a` flips it back so we don't pollute
# unrelated children.
set -a
# shellcheck source=/dev/null
. "$ENV_FILE"
set +a

: "${CITUS_DB_HOST:?CITUS_DB_HOST missing in $ENV_FILE}"
: "${CITUS_DB_PORT:?CITUS_DB_PORT missing in $ENV_FILE}"
: "${CITUS_DB_NAME:?CITUS_DB_NAME missing in $ENV_FILE}"
# M13: dump as citus_backup (BYPASSRLS), not citus_app. citus_app is
# the table owner + non-bypassrls, and FORCE row-level-security
# applies to owner roles — pg_dump as citus_app would fail with
# "query would be affected by row-level security policy". The
# upgrade.sh provisions both the role and these env vars; if they're
# missing the deploy hasn't run since M13 — re-run upgrade.sh.
: "${CITUS_BACKUP_DB_USER:?CITUS_BACKUP_DB_USER missing in $ENV_FILE — re-run upgrade.sh to provision the M13 backup role}"
: "${CITUS_BACKUP_DB_PASSWORD:?CITUS_BACKUP_DB_PASSWORD missing in $ENV_FILE — re-run upgrade.sh to provision the M13 backup role}"

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
dump="${BACKUP_DIR}/accounting-${timestamp}.dump.gz"
tmp="${dump}.partial"

# Custom format (-Fc) is what pg_restore consumes; gzip -9 because
# disk space is cheaper than the next backup window. Atomic rename
# at the end so a crash mid-dump doesn't leave a half-file that
# looks like a real backup.
PGPASSWORD="$CITUS_BACKUP_DB_PASSWORD" pg_dump \
  --host="$CITUS_DB_HOST" \
  --port="$CITUS_DB_PORT" \
  --username="$CITUS_BACKUP_DB_USER" \
  --format=custom \
  --no-owner \
  --no-privileges \
  "$CITUS_DB_NAME" \
  | gzip -9 > "$tmp"

mv "$tmp" "$dump"
size_bytes="$(stat -c%s "$dump")"
echo "wrote ${dump} (${size_bytes} bytes)"

# Retention sweep. mtime +N catches files modified more than N*24h ago.
removed=0
while IFS= read -r -d '' old; do
  rm -f "$old"
  echo "rotated ${old}"
  removed=$((removed + 1))
done < <(find "$BACKUP_DIR" -maxdepth 1 -name 'accounting-*.dump.gz' -mtime +"$RETENTION_DAYS" -print0)
echo "rotation: ${removed} file(s) older than ${RETENTION_DAYS} day(s) removed"
