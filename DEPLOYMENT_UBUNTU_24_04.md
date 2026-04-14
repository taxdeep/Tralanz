# Ubuntu 24.04 Deployment

This repository now includes two deployment entrypoints for Ubuntu 24.04:

- `install.sh`
- `upgrade.sh`

They target a single-host deployment with:

- `nginx`
- local `PostgreSQL`
- local frontend `SQLite`
- `Node.js 22.x`
- `.NET 11` from the Ubuntu 24.04 package feed
- optional Let's Encrypt HTTPS via `certbot`
- `systemd` services for:
  - Next.js frontend
  - `Citus.Accounting.Api`
  - `Citus.SysAdmin.Api`

## Usage

Fresh install:

```bash
sudo ./install.sh
```

Upgrade from the current checked-out source:

```bash
sudo ./upgrade.sh
```

During an interactive run, the script now asks:

- whether to enable HTTPS and request or renew a Let's Encrypt certificate
- whether to redirect HTTP traffic to HTTPS
- whether to start the Citus application services after deployment

## Assumptions

- the script is run from the repository checkout that should be deployed
- the host is Ubuntu 24.04
- deployment is HTTP-only unless HTTPS is enabled during the run or through env vars
- PostgreSQL runs on the same host

## Runtime layout

- source copy: `/opt/citus/source`
- published .NET apps: `/opt/citus/publish`
- runtime data: `/opt/citus/runtime`
- backups: `/opt/citus/backups`
- environment file: `/etc/citus/citus.env`
- ACME challenge webroot: `/var/www/certbot`

## Reverse proxy routes

- `/` -> Next.js frontend
- `/accounting` -> `Citus.Accounting.Api`
- `/core` -> `Citus.SysAdmin.Api`
- `/health/accounting`
- `/health/sysadmin`

## Important limitations

The repository is still in a transitional state, so the scripts are intentionally honest about database upgrades:

- frontend schema is synchronized with `prisma db push`
- backend PostgreSQL initialization uses `CITUS_POSTGRESQL_MIGRATION_DRAFT.sql`
- that backend SQL file is treated as a baseline, not a true incremental migration chain
- for that reason, the scripts only auto-apply the backend draft when the target database is empty and does not yet contain the `users` table
- if PostgreSQL already has tables but the `users` sentinel is missing, the scripts stop instead of guessing how to merge a partial schema

That means:

- first install is one-click
- later upgrades handle app deployment, frontend schema sync, service restarts, backups, and platform-core bootstrap
- but fully automated incremental backend PostgreSQL schema evolution should eventually move to proper versioned migrations

## First login

On first install, if the frontend SQLite database is empty, the script runs `prisma/seed.mjs` and creates:

- email: `owner@example.com`
- username: `owner`
- password: `password123`

## Customization

You can predefine values on first install:

```bash
sudo CITUS_SERVER_NAME=example.com \
     CITUS_ENABLE_HTTPS=1 \
     CITUS_CERTBOT_EMAIL=ops@example.com \
     CITUS_FRONTEND_PORT=3000 \
     CITUS_ACCOUNTING_API_PORT=5088 \
     CITUS_SYSADMIN_API_PORT=5089 \
     ./install.sh
```

After installation, edit `/etc/citus/citus.env` and rerun `sudo ./upgrade.sh` to apply changes to systemd and nginx.

For non-interactive automation, the useful variables are:

- `CITUS_SERVER_NAME`
- `CITUS_ENABLE_HTTPS=1|0`
- `CITUS_CERTBOT_EMAIL`
- `CITUS_HTTPS_REDIRECT=1|0`
- `CITUS_AUTO_START=1|0`

If HTTPS is enabled, `CITUS_SERVER_NAME` must be a public DNS name that already points to the server and is reachable on port `80`.

The deployment writes a Certbot renewal hook that reloads `nginx` after a successful renewal, so the renewed certificate can take effect without a manual restart.
