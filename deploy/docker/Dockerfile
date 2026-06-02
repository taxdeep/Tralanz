# syntax=docker/dockerfile:1.7

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
ARG NODE_IMAGE=node:20-bookworm-slim

# ---- frontend stage: Tailwind v4 → wwwroot/app.css ---------------------------
# Skipped for projects that don't ship CSS (the APIs); gated by FRONTEND_BUILD.
FROM ${NODE_IMAGE} AS frontend
WORKDIR /src

COPY backend/frontend/package.json backend/frontend/pnpm-lock.yaml backend/frontend/pnpm-workspace.yaml backend/frontend/.npmrc backend/frontend/
COPY backend/frontend/scripts ./backend/frontend/scripts
COPY backend/src/Citus.Ui.Shared/Styles ./backend/src/Citus.Ui.Shared/Styles
COPY backend/src/Citus.Business.Blazor/Styles ./backend/src/Citus.Business.Blazor/Styles
COPY backend/src/Citus.SysAdmin.Blazor/Styles ./backend/src/Citus.SysAdmin.Blazor/Styles
COPY backend/src/Citus.Business.Blazor/Components ./backend/src/Citus.Business.Blazor/Components
COPY backend/src/Citus.SysAdmin.Blazor/Components ./backend/src/Citus.SysAdmin.Blazor/Components
COPY backend/src/Citus.Ui.Shared ./backend/src/Citus.Ui.Shared

RUN corepack enable && corepack prepare pnpm@9.12.0 --activate \
    && cd backend/frontend \
    && NODE_ENV=development pnpm install --frozen-lockfile --prod=false \
    && pnpm run css:build

# ---- build stage: dotnet publish (consumes pre-built CSS) -------------------
FROM ${SDK_IMAGE} AS build
ARG PROJECT_PATH
ARG CONFIGURATION=Release
ARG FRONTEND_BUILD="false"

WORKDIR /src

COPY global.json ./
COPY backend ./backend

# Pull in the Tailwind output from the frontend stage so dotnet publish
# does not need pnpm. The MSBuild target is suppressed via SkipCssBuild.
COPY --from=frontend /src/backend/src/Citus.Business.Blazor/wwwroot/app.css ./backend/src/Citus.Business.Blazor/wwwroot/app.css
COPY --from=frontend /src/backend/src/Citus.SysAdmin.Blazor/wwwroot/app.css ./backend/src/Citus.SysAdmin.Blazor/wwwroot/app.css

RUN dotnet publish "$PROJECT_PATH" \
    -c "$CONFIGURATION" \
    -o /app/publish \
    -p:SkipCssBuild=true \
    /p:UseAppHost=false

# ---- runtime stage ----------------------------------------------------------
FROM ${RUNTIME_IMAGE} AS runtime
ARG PROJECT_DLL

WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
ENV APP_DLL=${PROJECT_DLL}

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["sh", "-c", "dotnet \"$APP_DLL\""]
