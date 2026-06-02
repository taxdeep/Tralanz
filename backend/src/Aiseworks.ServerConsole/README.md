# Aiseworks Server Console

Dedicated administrator desktop app for local/server operations.

This app is intentionally separate from `Aiseworks.DesktopShell`, which is the
ordinary user-facing WebView shell. Server lifecycle and database operations
belong here so normal users do not see stop/restart/backup controls.

## Run

```powershell
dotnet run --project backend/src/Aiseworks.ServerConsole/Aiseworks.ServerConsole.csproj
```

## Scope

Current scope:

- check PostgreSQL TCP connectivity
- check Accounting API, SysAdmin API, Business UI, and SysAdmin UI health
- start, stop, and restart the local Docker test stack
- start, stop, restart, and query a configured local Windows Service
- view recent Docker test-stack logs
- create a local Docker test database `.dump` backup
- persist last backup status under local app data

Remote LAN server lifecycle controls and production restore remain out of
scope. Restore should be added only after destructive overwrite checks,
compatibility validation, audit capture, and recovery guidance are designed.

The current Docker Compose file is a test full-stack runtime, not a database-only
container. It starts Postgres plus Accounting API, Business UI, SysAdmin API,
and SysAdmin UI. A future production split can move the server to a Windows
Service and keep PostgreSQL as a separate managed database.
