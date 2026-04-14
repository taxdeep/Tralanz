# WebVella ConsoleApp Adaptation

This note explains how `WebVella.Erp.ConsoleApp` was interpreted and translated into `Citus.ConsoleApp`.

## What `WebVella.Erp.ConsoleApp` controls

The source console app is a local operator control surface for the WebVella kernel.

Its `Program.cs` does four important things:

1. Initialize the ERP engine
   - load `Config.json`
   - initialize settings
   - open the database context
   - initialize AutoMapper
   - create system entities
   - register hooks

2. Open a privileged execution scope
   - use `SecurityContext.OpenSystemScope()`
   - run queries under a trusted system identity

3. Demonstrate read-side control
   - query users through EQL
   - let hooks reshape the effective query

4. Demonstrate write-side interception
   - create, update, and delete role records
   - observe pre/post hook callbacks

## Why Citus should not copy it literally

`Citus` does not use WebVella's generic record engine as its business core.

`Citus` already has:

- a dedicated accounting domain model
- a posting engine
- PostgreSQL business tables that are authoritative

So the correct translation is not “generic record CRUD console”, but “platform kernel console”.

## Citus translation

`Citus.ConsoleApp` keeps the same control-plane intention:

- local operator-oriented console entry point
- self-contained config file
- explicit platform bootstrap
- privileged inspection of the kernel metadata registry
- controlled mutation of platform metadata

Commands:

- `describe-webvella`
- `health`
- `bootstrap-core`
- `list-modules`
- `list-entities [moduleKey]`
- `show-entity <entityName>`
- `upsert-demo-entity [entityName]`

## Mapping

- `InitErpEngine` -> `bootstrap-core`
- trusted query sample -> `list-modules`, `list-entities`, `show-entity`
- hook/control sample -> `upsert-demo-entity`
- `Config.json` -> retained in `Citus.ConsoleApp`

## Outcome

The result is a console app that governs the Citus platform kernel the way the WebVella sample governs the WebVella kernel, while still respecting that accounting truth in Citus belongs to the posting engine instead of a generic metadata-record runtime.
