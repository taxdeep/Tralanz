# Citus Frontend Tooling

Node toolchain for the two Blazor Web Apps (`Citus.Business.Blazor`,
`Citus.SysAdmin.Blazor`). Owns Tailwind v4 builds and the Tabler icon
sprite. The Blazor projects themselves are still C#/Razor — this folder
exists only because Tailwind needs a Node CLI.

## Requirements

- Node 20+
- pnpm 9+

## Layout

```
backend/frontend/
  package.json           pnpm workspace root + Tailwind scripts
  pnpm-workspace.yaml    (no workspace packages today; reserved)
  scripts/
    sync-tabler-icons.mjs   pulls a fixed list of Tabler SVGs into the sprite
```

CSS sources live next to each Blazor project:

```
backend/src/Citus.Business.Blazor/Styles/app.css   <- Tailwind input
backend/src/Citus.Business.Blazor/wwwroot/app.css  <- build output (gitignored)
backend/src/Citus.SysAdmin.Blazor/Styles/app.css   <- Tailwind input
backend/src/Citus.SysAdmin.Blazor/wwwroot/app.css  <- build output (gitignored)
```

Shared design tokens, atoms, and the Tabler sprite live in
`backend/src/Citus.Ui.Shared/`.

## Common commands

```bash
pnpm install                  # one-time install
pnpm run css:build            # build both apps' CSS once (used by csproj target)
pnpm run css:watch            # watch both apps in parallel during dev
pnpm run icons:sync           # refresh tabler-sprite.svg from node_modules
```

## CI / Build hook

Each Blazor csproj has a `BuildCss` target that runs `pnpm run css:build:*`
before `BeforeBuild`. CI must install Node 20 and run `pnpm install --frozen-lockfile`
before `dotnet build`.
