# Tralanz Books — Sentry exception monitoring (optional)

This product ships with the Sentry SDK pre-wired into both API services
(`citus-accounting-api`, `citus-sysadmin-api`). The integration is
**off by default** — without a DSN, the SDK initializes empty and the
middleware is a no-op. Configure a DSN to enrol the host into a Sentry
project.

This is the V1 single-host setup. PII forwarding is intentionally off
(accounting payloads carry customer + amount data); flip
`Sentry__SendDefaultPii=true` if your incident-response process needs
the raw IP / cookies. Don't.

---

## Quick start

1. **Create a Sentry project**
   - Sign up at [sentry.io](https://sentry.io). Free tier (5 k events /
     month) covers a single-tenant pilot comfortably.
   - Create one project per environment if you run multiple
     deployments. The free tier allows multiple projects.
   - Copy the DSN — the long string that looks like
     `https://abc123@o12345.ingest.sentry.io/67890`.

2. **Add the DSN to the host's env file**

   ```bash
   sudo bash -c 'echo "Sentry__Dsn=https://abc123@o12345.ingest.sentry.io/67890" >> /etc/citus/citus.env'
   ```

   The convention is `Sentry__Dsn` (double underscore — the standard
   ASP.NET Core mapping for nested config keys). The legacy
   `SENTRY_DSN` is also accepted as a fallback.

3. **Restart the services** so they re-read the env file:

   ```bash
   sudo systemctl restart citus-accounting-api citus-sysadmin-api
   ```

4. **Trigger a test exception** to verify the wiring:

   ```bash
   curl -fsS http://127.0.0.1:5088/health   # baseline — should still 200
   # Then provoke a real 500 from a stale token / bad payload and watch
   # the Sentry project dashboard. The event should land within ~5 s.
   ```

---

## What gets captured

| Event | Captured by default |
|---|---|
| Unhandled exception in a request | yes |
| `ILogger.LogError` / `LogCritical` calls | yes (as breadcrumbs + an event when severity ≥ Error) |
| `ILogger.LogWarning` / `LogInformation` | as breadcrumbs only — they tag the next exception event but don't fire on their own |
| 4xx HTTP responses (validation rejections) | no — these are by design, not bugs |
| 5xx HTTP responses without a thrown exception | yes (Sentry treats 5xx as a signal) |
| Slow request (> 5 s) | partial — captured as a transaction with high `op.duration` |
| TaskCanceledException from a CancellationToken | yes, but easy to filter via the Sentry project's UI rules |

The **release tag** is the auto-bumped Citus version (e.g.
`0.00.000.0000.05.4M`). Sentry links each exception back to the build
that produced it, so a regression introduced by a specific commit shows
up in the version's release-overview page.

The **environment tag** follows ASP.NET Core's
`ASPNETCORE_ENVIRONMENT` (Development / Staging / Production). The
systemd EnvironmentFile sets this; check `/etc/citus/citus.env` if
your alerts read the wrong value.

---

## Tuning

The defaults are conservative — fine for the first month of pilot
traffic. If you outgrow the free tier (hard to do on a single tenant),
options:

- **Sample down tracing**: change `TracesSampleRate = 1.0` to `0.1` in
  both `Program.cs` files. Errors still ship at 100 %; only the
  request-trace transactions get sampled.
- **Drop noisy events**: in the Sentry project UI, add inbound filters
  for `OperationCanceledException` (most are benign disconnects) and
  expected `404 Not Found` paths.
- **Move to self-hosted**: Sentry's open-source server runs in Docker.
  Same SDK, different DSN. Out of scope for V1.

---

## Removing Sentry

If you decide not to use exception monitoring:

```bash
sudo sed -i '/^Sentry__Dsn=/d' /etc/citus/citus.env
sudo systemctl restart citus-accounting-api citus-sysadmin-api
```

The package still loads at startup but the SDK initializes inert. To
remove the dependency entirely, drop the `Sentry.AspNetCore`
PackageReference from both API csproj files and rebuild — but you'll
lose the ability to enable monitoring later without a redeploy.

---

## Verifying the wiring without a Sentry account

Run with a deliberately-malformed DSN to confirm the init path is
exercised but doesn't crash the host:

```bash
sudo bash -c 'echo "Sentry__Dsn=https://invalid@example.invalid/0" >> /etc/citus/citus.env'
sudo systemctl restart citus-accounting-api
sudo journalctl -u citus-accounting-api --since "30 seconds ago" | grep -i sentry
```

The SDK logs an "unable to send" warning and the API stays up. Strip
the DSN line back out and you're back to the no-op default.
