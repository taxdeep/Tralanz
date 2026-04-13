# Citus

QuickBooks-like accounting MVP scaffold using:
- Next.js
- TypeScript
- Tailwind CSS
- Prisma
- SQLite

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

## Migration Drafts

- [CITUS_POSTGRESQL_MIGRATION_DRAFT.sql](./CITUS_POSTGRESQL_MIGRATION_DRAFT.sql)

## Backend Skeleton

- [backend/README.md](./backend/README.md)
- [backend/Citus.Accounting.sln](./backend/Citus.Accounting.sln)

## Run locally

1. Install dependencies:
   - `npm install`
2. Create environment file:
   - copy `.env.example` to `.env`
3. Generate Prisma client:
   - `npm run prisma:generate`
4. Run first migration:
   - `npm run prisma:migrate -- --name init`
5. Seed a test user:
   - `npm run db:seed`
6. Start development server:
   - `npm run dev`
