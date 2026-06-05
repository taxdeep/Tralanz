# Tralanz Books

A .NET 10 + PostgreSQL accounting platform. The backend is a platform-core
runtime that registers system modules and entity metadata for the accounting
domain (GL / AR / AP / Inventory) and ships two Blazor Server shells —
`Citus.Business.Blazor` (the operator-facing books) and
`Citus.SysAdmin.Blazor` (tenant / first-company provisioning).

> **Naming note** — the product is **Tralanz Books**; internal projects still
> carry the legacy `Citus.*` / `citus_*` prefixes. The approved end-state is the
> whitelist root layout in
> [`NAMING_WHITELIST_REFACTOR_PLAN.md`](NAMING_WHITELIST_REFACTOR_PLAN.md): new
> code lands in the whitelist roots (`Web`, `Modules`, `Engines`,
> `Infrastructure`, `SharedKernel`, …), and the existing `Citus.*` projects are
> migrated into them in a dedicated structural batch. User-visible copy migrates
> to Tralanz eagerly; technical `Citus.*` names stay until that batch runs.
> Database identifiers (`citus_*`) are not renamed.

## Deployment paths

There are two supported ways to stand the stack up:

1. **[Ubuntu 24.04 install / upgrade scripts](#1-ubuntu-2404-deployment)** —
   the primary production path. Native systemd, nginx, Let's Encrypt.
2. **[Docker Compose](#2-docker-compose)** — for local exploration and
   reproducible dev / preview environments.

Both paths execute [CITUS_POSTGRESQL_MIGRATION_DRAFT.sql](./CITUS_POSTGRESQL_MIGRATION_DRAFT.sql)
on first DB initialization. Neither path seeds demo business data unless
the operator explicitly opts in.

---

## 1. Ubuntu 24.04 deployment

### Prerequisites

- A clean Ubuntu **24.04** host (other versions are rejected by the script).
- Root / sudo access.
- A public DNS name pointed at the host's IPv4 address if you intend to
  enable HTTPS.
- Outbound network for apt, the .NET install, snap (Certbot), pnpm, and
  the Microsoft package feed.

### First install

```bash
git clone https://github.com/taxdeep/Tralanz.git /root/citus
cd /root/citus
sudo bash ./deploy/ubuntu24/install.sh --domain books.example.com --ssl --email ops@example.com
```

> The scripts are committed with the executable bit set, so plain
> `sudo ./deploy/ubuntu24/install.sh ...` also works. The `sudo bash`
> form above is robust against checkouts that lost the `+x` bit (for
> example a Windows clone, or `git diff --cached` mode comparisons), so
> it is the recommended way to invoke them.

The interactive prompts cover HTTPS / certificate email / HTTP-to-HTTPS
redirect / auto-start of services. CLI flags override the prompts:

| Flag | Effect |
|---|---|
| `--domain NAME` (or `--server-name NAME`) | Public DNS name for nginx and Let's Encrypt |
| `--ssl` / `--no-ssl` | Enable / disable HTTPS automation |
| `--email ADDRESS` | Let's Encrypt contact email (required when `--ssl`) |
| `--redirect-http` / `--no-redirect-http` | Toggle HTTP → HTTPS redirect once a cert exists |
| `--start` / `--no-start` | Start (or leave stopped) Citus services after deployment |
| `--http-only` | Alias for `--no-ssl` |

`install.sh` does, in order: apt prep → install .NET 10 SDK + Node 20 →
create `citus` system user → write `/etc/citus/citus.env` →
provision Postgres role + database → publish the four .NET projects →
seed platform metadata via `Citus.ConsoleApp bootstrap-core` → write
systemd units → request the Let's Encrypt certificate (if `--ssl`) →
write nginx config → start services and run health checks.

What lands on the host:

| Path | Purpose |
|---|---|
| `/opt/citus/source` | rsync'd repository tree |
| `/opt/citus/publish/*` | published .NET binaries (4 services) |
| `/opt/citus/backups` | pg_dump snapshots taken before each upgrade |
| `/etc/citus/citus.env` | environment file consumed by every service |
| `/etc/nginx/sites-available/citus.conf` | reverse proxy config |
| `/etc/letsencrypt` | Let's Encrypt certs and renewal hooks (when SSL is on) |
| `/etc/systemd/system/citus-*.service` | four service units |

### Upgrade

```bash
cd /root/citus
git pull
sudo bash ./deploy/ubuntu24/upgrade.sh
```

`upgrade.sh` reuses the saved settings in `/etc/citus/citus.env`, so it
does **not** re-prompt for HTTPS / email. CLI flags work the same as
install. The script aborts early if the deployed version (read back from
the env file) already matches the repository's `VERSION`.

`upgrade.sh` also self-heals an incomplete env file: if
`CITUS_ACCOUNTING_DB` or any `CITUS_DB_*` part is missing, it composes
them from existing values (or fills in defaults) and re-aligns the
Postgres role password in the same pass.

### Reset back to first-run state

```bash
sudo bash ./deploy/ubuntu24/reset.sh        # interactive; type 'reset' to confirm
sudo bash ./deploy/ubuntu24/reset.sh --yes  # non-interactive
```

`reset.sh` drops every table in the public schema of the accounting
database, then restarts services (which re-EnsureSchema and reseed the
ISO 4217 currency catalog). It does **not** touch `/etc/citus/citus.env`,
nginx, TLS certs, or the Postgres role / password. After a reset, the
SysAdmin shell shows "Create first SysAdmin"; once that and First
Company Wizard run, the Business shell is ready for sign-in with the
real owner credentials.

### Where to sign in

| URL | Purpose |
|---|---|
| `https://books.example.com/` | Business shell (`Citus.Business.Blazor`) |
| `https://books.example.com/sysadmin/` | SysAdmin shell (first-time setup, tenant control) |
| `https://books.example.com/health/accounting` | Accounting API health |
| `https://books.example.com/health/sysadmin` | SysAdmin API health |

If `--ssl` is off the same paths are reachable over HTTP at the host's
IPv4.

### Demo data on Ubuntu

There is no demo seed mechanism in `install.sh` / `upgrade.sh`. The
`PlatformBootstrapFixturesInitializer` (Northwind / Blue Harbor / Alice /
Ben) is gated by `BusinessBootstrap:Fixtures:Enabled`, which
[`appsettings.Production.json`](backend/src/Citus.Accounting.Api/appsettings.Production.json)
sets to `false`. Production installs come up empty.

If you previously signed into a Business shell that did surface a
bootstrap session, clear `sessionStorage` on the affected origin
(DevTools → Application → Session Storage) before signing in again —
the front-end now discards stale `bootstrap:`-prefixed tokens in
non-Development environments, but a refresh on the same tab is still
the cleanest way to recover.

---

## 2. Docker Compose

The Compose stack is intended for local exploration and disposable preview
environments — it does not match the production deploy 1:1. It starts:

- PostgreSQL 16 (with `CITUS_POSTGRESQL_MIGRATION_DRAFT.sql` mounted as
  the first init script)
- `accounting-api` (`Citus.Accounting.Api`)
- `business-web` (`Citus.Business.Blazor`)
- `sysadmin-api` (`Citus.SysAdmin.Api`)
- `sysadmin-web` (`Citus.SysAdmin.Blazor`)

Files:

- [deploy/docker/compose.yml](./deploy/docker/compose.yml)
- [deploy/docker/Dockerfile](./deploy/docker/Dockerfile)
- [deploy/docker/.env.example](./deploy/docker/.env.example)

### Prepare the environment file

```bash
cp deploy/docker/.env.example deploy/docker/.env
```

```powershell
Copy-Item deploy/docker/.env.example deploy/docker/.env
```

Key values to review before bringing the stack up:

- `CITUS_DB_PASSWORD` — Postgres superuser password.
- `CITUS_TOTP_PROTECTION_KEY` — secret used to encrypt MFA factors.
- SMTP values, only if you want notification / verification flows.
- Any host-port collisions — `CITUS_WEB_PORT`, `CITUS_ACCOUNTING_API_PORT`,
  `CITUS_SYSADMIN_API_PORT`, `CITUS_SYSADMIN_WEB_PORT`, `CITUS_DB_PORT`.
- `CITUS_ENABLE_DEMO_SEED` — defaults to `false`. Flip to `true` only if
  you want the Northwind / Blue Harbor / Alice / Ben business records
  seeded on first DB initialization. The seed runs **once**, at the
  Postgres init step; rebuilding only the app containers will not undo
  it. To get back to a clean DB, delete the volume (see below).

### Build and start

```bash
docker compose --env-file deploy/docker/.env -f deploy/docker/compose.yml up -d --build
```

### Endpoints

- Business shell: <http://localhost:8080>
- SysAdmin web: <http://localhost:8090>
- Accounting API health: <http://localhost:5088/health>
- SysAdmin API health: <http://localhost:5089/health>
- SysAdmin web health: <http://localhost:8090/system/health>

### Demo sign-in (only when `CITUS_ENABLE_DEMO_SEED=true`)

The Compose-only seed inserts two business users you can sign in as:

- `alice.rowan@northwind.example` / `DemoPass123!` — owner of `Northwind Studio Ltd.`
- `ben.mercer@blueharbor.example` / `DemoPass123!` — user at `Blue Harbor Trading Co.`

These are dev fixtures. They must not be enabled in any non-demo
environment.

### Stop, restart, reset

```bash
# stop containers
docker compose --env-file deploy/docker/.env -f deploy/docker/compose.yml down

# stop and delete the Postgres volume (re-runs init scripts on next up)
docker compose --env-file deploy/docker/.env -f deploy/docker/compose.yml down -v
```

### Notes

- The Compose stack is not a hardened production cluster design.
- `business-web` and `sysadmin-web` are exposed on separate ports rather
  than fronted by a gateway. If you want a single domain + TLS, place
  Nginx / Caddy / Traefik in front and keep the internal container URLs
  unchanged.

---

## Product authority and design specs

The highest-priority product and engineering authority is:

- [CITUS_PRODUCT_ENGINEERING_AUTHORITY.md](./CITUS_PRODUCT_ENGINEERING_AUTHORITY.md)

Related architecture / design documents:

- [POSTING_ENGINE_MULTICURRENCY_DESIGN.md](./POSTING_ENGINE_MULTICURRENCY_DESIGN.md)
- [PROJECT_RULES.md](./PROJECT_RULES.md)

## Executable specs

- [MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md](./MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md)
- [POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md](./POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md)
- [AP_AR_LIFECYCLE_CONTROL_SPEC.md](./AP_AR_LIFECYCLE_CONTROL_SPEC.md)
- [UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md](./UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md)
- [POSTGRESQL_CORE_SCHEMA_SPEC.md](./POSTGRESQL_CORE_SCHEMA_SPEC.md)
- [CSHARP_DOMAIN_AND_APPLICATION_SKELETON.md](./CSHARP_DOMAIN_AND_APPLICATION_SKELETON.md)
- [WEBVELLA_CORE_ADAPTATION.md](./WEBVELLA_CORE_ADAPTATION.md)
- [WEBVELLA_CONSOLEAPP_ADAPTATION.md](./WEBVELLA_CONSOLEAPP_ADAPTATION.md)
- [BLAZOR_PHASE1_MERGE_PLAN.md](./BLAZOR_PHASE1_MERGE_PLAN.md)

## Migration drafts

- [CITUS_POSTGRESQL_MIGRATION_DRAFT.sql](./CITUS_POSTGRESQL_MIGRATION_DRAFT.sql)

## Backend skeleton

- [backend/README.md](./backend/README.md)
- [backend/Citus.Accounting.sln](./backend/Citus.Accounting.sln)

## Running tests locally

The Postgres-backed test suites (`Tests.AR`, `Tests.AP`, `Tests.GL`,
`Tests.CompanyAccess`) connect to a developer-local `citus_accounting`
database via the `CITUS_ACCOUNTING_DB` env var (default
`Host=localhost;Port=5432;...;Password=change-me`). Unlike the
production deploy path, dev does NOT auto-apply
`deploy/migrations/*.sql` to that DB — so after pulling new migrations
you need to sync the dev DB once:

```pwsh
pwsh tools/sync-test-db.ps1
```

Optional flag if the permission-foundation migration aborts because
seed companies have no active owner:

```pwsh
pwsh tools/sync-test-db.ps1 -SeedC000001Owner
```

The script is idempotent (uses the same `schema_migrations` tracking
table the production runner uses) and soft-fails per-migration so a
single broken file doesn't stop the chain.

Known dev-only "expected failure":
[`2026-05-22-m13-row-level-security.sql`](./deploy/migrations/2026-05-22-m13-row-level-security.sql)
attaches RLS policies to the `citus_app` role, which only the
production `install.sh` provisions. Local dev runs as superuser, so
this migration's `GRANT` statements fail with `42704`. The test
suites that don't enter strict-mode RLS continue to work without it.
