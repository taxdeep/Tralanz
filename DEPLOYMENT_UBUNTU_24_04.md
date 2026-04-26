# Ubuntu 24.04 Deployment

This repository includes two Ubuntu 24.04 deployment entrypoints:

- `install.sh`
- `upgrade.sh`

They target a single-host deployment with:

- `nginx`
- local `PostgreSQL`
- `.NET 11 preview / C# 15 preview` installed into `/opt/dotnet`
  - first through the official `dotnet-install.sh` script
  - if Ubuntu 24.04 cannot resolve that exact preview cleanly, the script now falls back to downloading the SDK tarball directly from `builds.dotnet.microsoft.com`
- optional Let's Encrypt HTTPS via `certbot`
- `systemd` services for:
  - `Web.Shell` Blazor frontend
  - `Citus.Accounting.Api`
  - `Citus.SysAdmin.Api`

## Usage

Fresh install with prompts:

```bash
sudo ./install.sh
```

Fresh install with one command, domain, and HTTPS:

```bash
sudo ./install.sh \
  --domain app.example.com \
  --ssl \
  --email ops@example.com
```

Upgrade from the current checked-out source:

```bash
sudo ./upgrade.sh
```

Upgrade while changing domain / SSL settings:

```bash
sudo ./upgrade.sh \
  --domain app.example.com \
  --ssl \
  --email ops@example.com
```

Version-aware upgrade behavior:

- the repository version is read from the root `VERSION` file
- the deployed version is stored as `CITUS_APP_VERSION` in `/etc/citus/citus.env`
- if both versions match, `upgrade.sh` skips the deployment before services are stopped or backups are taken

Supported CLI options:

- `--domain NAME` / `--server-name NAME`
- `--ssl` / `--https`
- `--no-ssl` / `--no-https` / `--http-only`
- `--email ADDRESS` / `--certbot-email ADDRESS`
- `--redirect-http` / `--no-redirect-http`
- `--start` / `--no-start`
- `--help`

You can also predefine values through environment variables:

```bash
sudo CITUS_SERVER_NAME=app.example.com \
     CITUS_ENABLE_HTTPS=1 \
     CITUS_CERTBOT_EMAIL=ops@example.com \
     CITUS_APT_FORCE_IPV4=1 \
     CITUS_APT_PRIMARY_MIRROR=http://archive.ubuntu.com/ubuntu \
     CITUS_FRONTEND_PORT=3000 \
     CITUS_ACCOUNTING_API_PORT=5088 \
     CITUS_SYSADMIN_API_PORT=5089 \
     ./install.sh
```

## Assumptions

- the script is run from the repository checkout that should be deployed
- the host is Ubuntu 24.04
- PostgreSQL runs on the same host
- HTTPS automation requires a public DNS name already pointing to the server
- ports `80` and `443` must be reachable from the public internet when requesting Let's Encrypt certificates

## Runtime layout

- source copy: `/opt/citus/source`
- published .NET apps: `/opt/citus/publish`
- runtime data: `/opt/citus/runtime`
- backups: `/opt/citus/backups`
- .NET install root: `/opt/dotnet`
- environment file: `/etc/citus/citus.env`
- ACME challenge webroot: `/var/www/certbot`

## Reverse proxy routes

- `/` -> `Web.Shell`
- `/accounting` -> `Citus.Accounting.Api`
- `/core` -> `Citus.SysAdmin.Api`
- `/health/accounting`
- `/health/sysadmin`

## HTTPS behavior

When HTTPS is enabled, the script:

- writes an HTTP nginx config first so ACME HTTP-01 validation can pass
- requests or renews a Let's Encrypt certificate with Certbot
- rewrites nginx with the SSL server block
- optionally redirects HTTP to HTTPS
- installs a renewal deploy hook that reloads nginx after certificate renewal

If `CITUS_SERVER_NAME` is `_`, `localhost`, an IP address, or otherwise not a public DNS name, HTTPS automation is skipped.

## Important limitations

The repository is still in a transitional state, so database upgrades are intentionally conservative:

- backend PostgreSQL initialization uses `CITUS_POSTGRESQL_MIGRATION_DRAFT.sql`
- that backend SQL file is treated as a baseline, not a true incremental migration chain
- first install auto-applies the backend draft only when the target database is empty and does not yet contain the `users` table
- if PostgreSQL already has tables but the `users` sentinel is missing, the scripts stop instead of guessing how to merge a partial schema

That means:

- first install is intended to be one-command
- later upgrades handle app deployment, service restarts, backups, and platform-core bootstrap
- fully automated incremental backend PostgreSQL schema evolution should eventually move to proper versioned migrations

## Current product-surface note

The deployment now serves `Web.Shell` as the primary UI because the active AR/AP, GL, CompanyAccess, book-governance, and mapping-control work is in the Blazor shell.

Current usability caveat:

- `Web.Shell` still uses CompanyAccess/bootstrap shell context rather than a production ABP Identity login flow
- this is useful for internal/demo/operator validation
- it should not yet be treated as production-auth complete

## Useful environment variables

- `CITUS_SERVER_NAME`
- `CITUS_ENABLE_HTTPS=1|0`
- `CITUS_CERTBOT_EMAIL`
- `CITUS_HTTPS_REDIRECT=1|0`
- `CITUS_AUTO_START=1|0`
- `CITUS_FRONTEND_PORT`
- `CITUS_ACCOUNTING_API_PORT`
- `CITUS_SYSADMIN_API_PORT`
- `CITUS_DB_NAME`
- `CITUS_DB_USER`
- `CITUS_DB_PASSWORD`
- `CITUS_DOTNET_SDK_VERSION`
- `CITUS_DOTNET_CHANNEL`
- `CITUS_DOTNET_QUALITY`
- `CITUS_DOTNET_SDK_TARBALL_URL`
- `CITUS_APT_FORCE_IPV4=1|0`
- `CITUS_APT_RETRIES`
- `CITUS_APT_HTTP_TIMEOUT`
- `CITUS_APT_PRIMARY_MIRROR`
- `CITUS_APT_SECURITY_MIRROR`

## Apt mirror resilience

The Ubuntu 24.04 scripts now harden apt by default:

- force IPv4 unless `CITUS_APT_FORCE_IPV4=0`
- apply apt retries and HTTP/HTTPS timeouts
- rewrite Ubuntu archive URIs to `CITUS_APT_PRIMARY_MIRROR` before package install

Default mirrors:

- primary: `http://archive.ubuntu.com/ubuntu`
- security: `http://security.ubuntu.com/ubuntu`

If your host has regional mirror routing problems, rerun install like this:

```bash
sudo CITUS_APT_FORCE_IPV4=1 \
     CITUS_APT_PRIMARY_MIRROR=http://archive.ubuntu.com/ubuntu \
     CITUS_APT_SECURITY_MIRROR=http://security.ubuntu.com/ubuntu \
     ./install.sh
```

After installation, edit `/etc/citus/citus.env` and rerun `sudo ./upgrade.sh` to apply changes to systemd and nginx.

## .NET 10 install path on Ubuntu 24.04

Ubuntu 24.04's default apt repositories do not ship `.NET 10`, so the deployment scripts register Microsoft's apt feed (`packages.microsoft.com`) and install the SDK from there. `/opt/dotnet` is symlinked to `/usr/share/dotnet` (where the apt package lands) so every downstream reference (systemd `DOTNET_ROOT`, `dotnet publish`) keeps working unchanged.

The order is:

1. **apt path (preferred for GA releases)** — register `packages-microsoft-prod`, then `apt install dotnet-sdk-10.0 aspnetcore-runtime-10.0`
2. exact SDK install through `dotnet-install.sh`
3. direct SDK tarball fallback from `builds.dotnet.microsoft.com`
4. channel fallback only if all of the above fail and `CITUS_DOTNET_ALLOW_CHANNEL_FALLBACK=1`

The apt path is automatically skipped (and the script falls through to `dotnet-install.sh`) when:

- `CITUS_DOTNET_QUALITY` is anything other than `GA` (e.g. `preview`, `daily`, `signed`)
- `CITUS_DOTNET_FORCE_MANUAL_INSTALL=1` is set explicitly
- The Microsoft apt feed is unreachable, or does not carry `dotnet-sdk-10.0`

For the current repository `global.json`, the manual-fallback URL resolves to:

```text
https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.100/dotnet-sdk-10.0.100-linux-x64.tar.gz
```

If you need to override that source manually, set:

```bash
sudo CITUS_DOTNET_SDK_TARBALL_URL=https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.100/dotnet-sdk-10.0.100-linux-x64.tar.gz \
     CITUS_DOTNET_FORCE_MANUAL_INSTALL=1 \
     ./install.sh
```

If you see this apt error during the apt path:

```text
E: Unable to locate package dotnet-sdk-10.0
E: Unable to locate package aspnetcore-runtime-10.0
```

it usually means Microsoft's apt feed has not been registered for this host. The installer registers it automatically on first run; if that step failed (network or DNS issue), the script logs `Unable to download packages-microsoft-prod.deb …` and falls through to the manual install path. Re-run `./upgrade.sh` after fixing connectivity, or set `CITUS_DOTNET_FORCE_MANUAL_INSTALL=1` to skip apt entirely.

If `/opt/dotnet` already contains a `.NET 11 preview` install from earlier, the apt path moves it aside to `/opt/dotnet.preview-<timestamp>` and replaces the path with the symlink; remove the backup directory once you have confirmed the new install is working.
