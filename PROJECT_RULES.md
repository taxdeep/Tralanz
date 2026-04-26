# PROJECT_RULES

This file is a lightweight implementation companion, not the top authority.

## Authority Order

The mandatory priority order for product and engineering decisions is:

`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > other requirement docs > task-specific notes > temporary implementation habits`

The highest-priority governing document is:

- [CITUS_PRODUCT_ENGINEERING_AUTHORITY.md](./CITUS_PRODUCT_ENGINEERING_AUTHORITY.md)

Related subordinate design documents currently include:

- [POSTING_ENGINE_MULTICURRENCY_DESIGN.md](./POSTING_ENGINE_MULTICURRENCY_DESIGN.md)
- [MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md](./MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md)
- [POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md](./POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md)
- [AP_AR_LIFECYCLE_CONTROL_SPEC.md](./AP_AR_LIFECYCLE_CONTROL_SPEC.md)
- [UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md](./UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md)
- [POSTGRESQL_CORE_SCHEMA_SPEC.md](./POSTGRESQL_CORE_SCHEMA_SPEC.md)
- [CSHARP_DOMAIN_AND_APPLICATION_SKELETON.md](./CSHARP_DOMAIN_AND_APPLICATION_SKELETON.md)
- [PRODUCT_PLAN.md](./PRODUCT_PLAN.md)
- [ACCOUNTING_MVP_PLANNING.md](./ACCOUNTING_MVP_PLANNING.md)
- [AI_PRODUCT_ARCHITECTURE.md](./AI_PRODUCT_ARCHITECTURE.md)

## Lightweight Working Rules

- Keep implementation modular and engine-centric.
- Do not bypass company isolation, posting rules, or backend-owned accounting truth.
- Treat cache, provider data, and AI as subordinate layers only.
- Preserve auditability and historical honesty when data is uncertain.
- All AI / unityAI surfaces must comply with [AI_PRODUCT_ARCHITECTURE.md](./AI_PRODUCT_ARCHITECTURE.md): provider-agnostic gateway, company-scoped learning, pending-by-default suggestions, and full decision traceability.

## Communication Rules For Major Changes

- Explain major code changes clearly.
- Explain database/schema impact clearly.
- Explain testing/verification steps clearly.
