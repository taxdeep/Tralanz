# WebVella Core Adaptation

This note captures what was taken from `WebVella.Erp` and how it was adapted into `Citus`.

## Source analysis

The `WebVella.Erp` package is not primarily an accounting engine. Its core is a metadata-driven ERP kernel:

- `ERPService.cs` bootstraps system tables, entities, relations, plugins, jobs, and platform data.
- `Api/EntityManager.cs` persists entity and field definitions as metadata.
- `Api/RecordManager.cs` provides generic record CRUD and relation orchestration on top of that metadata.
- `Api/SecurityManager.cs` builds identity and role management on the same core entity model.
- `ErpPlugin.cs` lets business modules register themselves into the kernel.

Useful source entry points:

- https://github.com/WebVella/WebVella-ERP/blob/master/WebVella.Erp/ERPService.cs
- https://github.com/WebVella/WebVella-ERP/blob/master/WebVella.Erp/Api/EntityManager.cs
- https://github.com/WebVella/WebVella-ERP/blob/master/WebVella.Erp/Api/RecordManager.cs
- https://github.com/WebVella/WebVella-ERP/blob/master/WebVella.Erp/Api/SecurityManager.cs
- https://github.com/WebVella/WebVella-ERP/blob/master/WebVella.Erp/ErpPlugin.cs

## Citus adaptation

`Citus` already had a strong accounting domain and posting engine. Replacing that with WebVella's generic record layer would have been the wrong move.

Instead, the adaptation makes the **WebVella-style platform kernel** the new system core:

- module registry
- entity metadata registry
- platform bootstrapper
- system admin API surface for governing the registry

The accounting bounded context now sits conceptually as a registered platform module instead of being the whole system.

## Implemented pieces

- `backend/src/Citus.Platform.Core`
  - platform module manifests
  - entity and field metadata models
  - metadata normalization service
  - platform bootstrap service
- `backend/src/Citus.Platform.Infrastructure`
  - PostgreSQL persistence for `platform_modules` and `platform_entities`
- `backend/src/Citus.SysAdmin.Api`
  - `POST /core/bootstrap`
  - `GET /core`
  - `GET /core/modules`
  - `GET /core/entities`
  - `GET /core/entities/{name}`
  - `POST /core/entities`
- `backend/src/Citus.Accounting.Api`
  - now reports itself as an `accounting` module registered through `Citus.Platform.Core`

## Why this shape

This keeps the strongest part of `Citus` intact:

- accounting truth still flows through the Posting Engine
- PostgreSQL accounting tables remain authoritative
- system composition is now driven by a reusable platform core instead of ad-hoc API ownership

That is the closest high-value translation of the `WebVella.Erp` core into this codebase without regressing the existing accounting architecture.
