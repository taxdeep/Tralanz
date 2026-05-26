# Aiseworks Desktop Shell

This is the first native desktop shell for Aiseworks.

It hosts the existing local web application through Microsoft Edge WebView2.
The shell does not connect directly to PostgreSQL and does not own accounting
business rules.

## Run

Start the local Aiseworks test stack first, then run:

```powershell
dotnet run --project backend/src/Aiseworks.DesktopShell/Aiseworks.DesktopShell.csproj
```

Default endpoints:

- Business UI: `http://localhost:18080`
- SysAdmin UI: `http://localhost:18090`

## Scope

Current scope:

- desktop window and navigation chrome
- Business and SysAdmin switches
- reload, back, forward, home, and direct URL entry
- selected server profile selector for navigation and health targets
- Business and SysAdmin reachability checks
- Postgres, Accounting API, Business UI, SysAdmin API, and SysAdmin UI health checks
- open Business UI in the default browser
- clear server-unavailable page when local services are offline
- initial DesktopBridge message channel between WPF WebView2 and Business Web UI
- `System -> Bridge test` command for validating shell-to-web and web-to-shell messaging

Server lifecycle, Docker logs, and database backup controls live in the
separate `Aiseworks.ServerConsole` administrator app. The user-facing desktop
shell must not expose start, stop, restart, or backup actions.

## DesktopBridge

The shell and Business Web UI use WebView2 web messages for the first integration
bridge:

- WPF sends command envelopes with channel `aiseworks.desktopBridge` and
  direction `shell-to-web`.
- Web UI sends event/request envelopes with the same channel and direction
  `web-to-shell`.
- Business Web UI exposes `window.AiseworksDesktopBridge` for future Blazor
  interop and dispatches host commands as `aiseworks:desktop-command` browser
  events.
- The initial validation flow is `System -> Bridge test`: WPF sends
  `shell.ping`, Web UI returns `web.pong`, and the desktop status bar updates.

The default profile is `Local Docker Test Stack`. Additional server profiles can
be added in `appsettings.json`, but this shell uses them only for navigation and
health display.

Profile selection is currently session-only. It does not rewrite
`appsettings.json`; a production settings workflow should be added later.

Future scope:

- server discovery and production health checks
- persisted server profile settings
- native print/export integration
- installer and service registration
