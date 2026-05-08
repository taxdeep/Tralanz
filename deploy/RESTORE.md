# Tralanz Books — PostgreSQL restore runbook

This document covers two scenarios:

1. **Routine restore** — the database is healthy but you want to roll back
   to last night (e.g. someone deleted a critical row).
2. **Disaster recovery** — the database is gone and you're rebuilding on
   a fresh host.

Backups are produced by `citus-backup.timer` daily at 03:00 UTC. They
live at `/opt/citus/backups/accounting-YYYYMMDDTHHMMSSZ.dump.gz` with a
7-day rolling retention (configurable via `CITUS_BACKUP_RETENTION_DAYS`).

The dump format is `pg_dump --format=custom`, gzipped. Restore consumes
it with `pg_restore`.

---

## Prerequisites

- Root or `sudo` on the target host.
- The same Postgres major version family that produced the dump (the
  installer pins Postgres 16; cross-major restore is possible but
  requires `pg_dumpall`-style migration, not covered here).
- Enough disk space for `/opt/citus/backups/` × 2 — restore briefly
  needs the original dump plus the live database.

---

## Scenario 1 — Routine restore (database still alive)

**You want this when:** an operator deleted / corrupted data today and
you want to roll back to last night's snapshot, but the rest of the
database is fine.

> ⚠️ This **replaces the entire `citus_accounting` database** with the
> dump. Any work done after the dump's timestamp is lost. If you only
> need a single table or row back, see "Surgical restore" below.

```bash
# 1. Pick the snapshot you want.
ls -la /opt/citus/backups/
# accounting-20260507T030000Z.dump.gz   (last night)
# accounting-20260506T030000Z.dump.gz   (the night before)
# ...

DUMP=/opt/citus/backups/accounting-20260507T030000Z.dump.gz

# 2. Stop the apps so nothing writes during restore.
sudo systemctl stop citus-accounting-api citus-web citus-sysadmin-api citus-sysadmin-web

# 3. Drop + recreate the target database. The role and grants survive.
sudo -u postgres psql -c "DROP DATABASE IF EXISTS citus_accounting;"
sudo -u postgres psql -c "CREATE DATABASE citus_accounting OWNER citus_app;"

# 4. Restore. --no-owner / --no-privileges match what the dump was
#    written with so we don't try to set ownership we don't have.
gunzip -c "$DUMP" | sudo -u postgres pg_restore \
  --dbname=citus_accounting \
  --no-owner \
  --no-privileges \
  --verbose

# 5. Restart the apps.
sudo systemctl start citus-accounting-api citus-web citus-sysadmin-api citus-sysadmin-web

# 6. Verify. Login should work, the dashboards should populate.
curl -fsS http://127.0.0.1:5088/health
```

---

## Scenario 2 — Disaster recovery (fresh host)

**You want this when:** the original host is gone (drive failure,
provider outage, ransomware). You have a backup file from somewhere
(off-site copy, S3, NAS). Goal: stand a new host up, restore the data,
get traffic flowing.

```bash
# 1. Provision the new host. Run the installer first — it creates
#    the role, the empty database, the systemd units, etc.
curl -fsSL https://raw.githubusercontent.com/taxdeep/Tralanz/main/deploy/ubuntu24/install.sh | sudo bash

# 2. Copy the backup file onto the new host. Adjust path as needed.
scp accounting-20260507T030000Z.dump.gz root@new-host:/opt/citus/backups/

# 3. Restore — same command as Scenario 1 step 4.
gunzip -c /opt/citus/backups/accounting-20260507T030000Z.dump.gz \
  | sudo -u postgres pg_restore \
      --dbname=citus_accounting \
      --no-owner \
      --no-privileges \
      --clean \
      --if-exists \
      --verbose

# 4. The installer started the systemd units already. They were
#    pointing at an empty schema; bounce them so they re-init against
#    the now-restored data.
sudo systemctl restart citus-accounting-api citus-web

# 5. Re-create operator login if needed. The first login uses the
#    sysadmin bootstrap password from /etc/citus/citus.env.
curl -fsS http://127.0.0.1:5088/health
```

---

## Surgical restore — single table / row

When you don't want to blow away the whole database:

```bash
# 1. Restore to a temporary schema / database, not the live one.
sudo -u postgres createdb citus_accounting_restore
gunzip -c /opt/citus/backups/accounting-YYYYMMDDTHHMMSSZ.dump.gz \
  | sudo -u postgres pg_restore \
      --dbname=citus_accounting_restore \
      --no-owner --no-privileges

# 2. Cherry-pick the rows you need. Examples:
sudo -u postgres psql citus_accounting_restore \
  -c "COPY (SELECT * FROM customers WHERE company_id='C000001' AND id='<uuid>') TO STDOUT" \
  | sudo -u postgres psql citus_accounting -c "COPY customers FROM STDIN"

# 3. Or use `psql -c "INSERT INTO ... SELECT ..."` across `dblink` /
#    `postgres_fdw` if the table is small enough to query directly.

# 4. Drop the temporary database.
sudo -u postgres dropdb citus_accounting_restore
```

---

## Verifying the backup chain

Run this monthly (or after any server work) to make sure backups are
flowing and restorable:

```bash
# Are timer + service healthy?
systemctl status citus-backup.timer
systemctl status citus-backup.service
journalctl -u citus-backup --since "2 days ago" | tail -20

# Are recent dumps the size you'd expect (i.e. growing roughly with
# the live database)?
ls -lah /opt/citus/backups/

# Smoke-test a restore on a scratch database. This catches "the dump
# is corrupt" and "the dump is from a different schema version" early.
sudo -u postgres createdb citus_accounting_smoke
gunzip -c /opt/citus/backups/$(ls -t /opt/citus/backups/ | head -1) \
  | sudo -u postgres pg_restore --dbname=citus_accounting_smoke --no-owner --no-privileges --verbose 2>&1 | tail -20
sudo -u postgres psql citus_accounting_smoke -c "SELECT count(*) FROM companies;"
sudo -u postgres dropdb citus_accounting_smoke
```

---

## Off-site copy (recommended for production)

The on-host backup defends against accidental delete and small file
corruption. It does **not** defend against drive failure, host loss,
or ransomware. For real protection:

- **Cheap option**: `rsync /opt/citus/backups/` to a second host nightly
  (after the local backup finishes — fire from cron / another timer).
- **Cloud option**: install the `s3cmd` / `aws-cli` and pipe a copy
  out of `citus-backup.sh` (a fork is fine — keep the local copy too).
- **Best option**: WAL archiving + base backup with a tool like `wal-g`
  or `barman`. Gets you point-in-time recovery (PITR) at minute
  granularity instead of "last night at 3am". Out of scope for V1 but
  on the roadmap.

---

## Backup schedule + retention reference

| Setting | Default | How to change |
|---|---|---|
| Trigger | 03:00 UTC daily | Edit `OnCalendar=` in `citus-backup.timer` |
| Retention | 7 days | `CITUS_BACKUP_RETENTION_DAYS=N` in `/etc/citus/citus.env` |
| Location | `/opt/citus/backups/` | `CITUS_BACKUP_DIR=/path` in `/etc/citus/citus.env` |
| Format | `pg_dump --format=custom`, gzipped | Hardcoded in `citus-backup.sh` (custom format is what `pg_restore` consumes) |
| Catch-up after downtime | Yes | `Persistent=true` in `citus-backup.timer` |
