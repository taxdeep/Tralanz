# Citus

The current backend direction also includes a `.NET 11 + C# 15` + PostgreSQL platform core that registers system modules and entity metadata for the accounting backend.

## Docker Deployment

The repository now includes a Docker Compose deployment for the current runtime surface:

- PostgreSQL
- `Citus.Accounting.Api`
- `Web.Shell`
- `Citus.SysAdmin.Api`
- `Citus.SysAdmin.Blazor`

Files:

- [deploy/docker/compose.yml](./deploy/docker/compose.yml)
- [deploy/docker/Dockerfile](./deploy/docker/Dockerfile)
- [deploy/docker/.env.example](./deploy/docker/.env.example)

### 1. Prepare environment values

Copy the sample environment file and adjust at least the database password and TOTP protection key:

```powershell
Copy-Item deploy/docker/.env.example deploy/docker/.env
```

```bash
cp deploy/docker/.env.example deploy/docker/.env
```

Important values:

- `CITUS_DB_PASSWORD`
- `CITUS_TOTP_PROTECTION_KEY`
- optional SMTP values if you want email delivery / verification flows
- if any host port is already occupied, change the published port values in `deploy/docker/.env`
- `CITUS_ENABLE_DEMO_SEED=true` will create a usable demo business/company dataset on first database initialization

### 2. Build and start the stack

```bash
docker compose --env-file deploy/docker/.env -f deploy/docker/compose.yml up -d --build
```

### 3. Open the services

- Business shell: `http://localhost:8080`
- Accounting API health: `http://localhost:5088/health`
- SysAdmin API health: `http://localhost:5089/health`
- SysAdmin web: `http://localhost:8090`
- SysAdmin web health: `http://localhost:8090/system/health`

### 4. First-run behavior

On the first run with an empty PostgreSQL volume:

- PostgreSQL executes [CITUS_POSTGRESQL_MIGRATION_DRAFT.sql](./CITUS_POSTGRESQL_MIGRATION_DRAFT.sql)
- if `CITUS_ENABLE_DEMO_SEED=true`, PostgreSQL also executes [deploy/docker/010-demo-seed.sh](./deploy/docker/010-demo-seed.sh)
- `SysAdmin.Api` can provision the first sysadmin account from the setup flow
- if you explicitly enable bootstrap sysadmin in `deploy/docker/.env`, Docker can also provision that account at startup

### 5. Demo business sign-in

When `CITUS_ENABLE_DEMO_SEED=true`, the first PostgreSQL initialization also seeds these business demo accounts:

- `alice.rowan@northwind.example` / `DemoPass123!`
  - company: `Northwind Studio Ltd.`
  - role: `owner`
- `ben.mercer@blueharbor.example` / `DemoPass123!`
  - company: `Blue Harbor Trading Co.`
  - role: `user`

This seed is for local/demo Docker usage only. Replace it or disable it for any non-demo environment.

### 6. Current limitation

- the Docker stack now seeds enough business data for sign-in, but it still does **not** provision a full production-grade tenant/company onboarding workflow
- it is meant to get the current repository running and explorable, not to replace governed business setup flows

### 7. Stop, restart, and reset

Stop containers:

```bash
docker compose --env-file deploy/docker/.env -f deploy/docker/compose.yml down
```

Stop and delete the PostgreSQL volume as well:

```bash
docker compose --env-file deploy/docker/.env -f deploy/docker/compose.yml down -v
```

When you remove the volume, the draft PostgreSQL initialization script runs again on the next `up`.

### 8. Notes

- This Compose setup is a practical repo deployment path, not a hardened production cluster design.
- `Web.Shell` and `SysAdmin.Blazor` are exposed on separate ports instead of being fronted by a gateway/reverse proxy.
- If you want domain + TLS, place Nginx / Caddy / Traefik in front of these services and keep the internal container-to-container URLs unchanged.

## Product Authority

The highest-priority product and engineering authority is:

- [CITUS_PRODUCT_ENGINEERING_AUTHORITY.md](./CITUS_PRODUCT_ENGINEERING_AUTHORITY.md)

Related architecture/design documents:

- [POSTING_ENGINE_MULTICURRENCY_DESIGN.md](./POSTING_ENGINE_MULTICURRENCY_DESIGN.md)
- [PROJECT_RULES.md](./PROJECT_RULES.md)

## Executable Specs

- [MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md](./MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md)
- [POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md](./POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md)
- [AP_AR_LIFECYCLE_CONTROL_SPEC.md](./AP_AR_LIFECYCLE_CONTROL_SPEC.md)
- [UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md](./UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md)
- [POSTGRESQL_CORE_SCHEMA_SPEC.md](./POSTGRESQL_CORE_SCHEMA_SPEC.md)
- [CSHARP_DOMAIN_AND_APPLICATION_SKELETON.md](./CSHARP_DOMAIN_AND_APPLICATION_SKELETON.md)
- [WEBVELLA_CORE_ADAPTATION.md](./WEBVELLA_CORE_ADAPTATION.md)
- [WEBVELLA_CONSOLEAPP_ADAPTATION.md](./WEBVELLA_CONSOLEAPP_ADAPTATION.md)
- [BLAZOR_PHASE1_MERGE_PLAN.md](./BLAZOR_PHASE1_MERGE_PLAN.md)

## Migration Drafts

- [CITUS_POSTGRESQL_MIGRATION_DRAFT.sql](./CITUS_POSTGRESQL_MIGRATION_DRAFT.sql)

## Backend Skeleton

- [backend/README.md](./backend/README.md)
- [backend/Citus.Accounting.sln](./backend/Citus.Accounting.sln)
