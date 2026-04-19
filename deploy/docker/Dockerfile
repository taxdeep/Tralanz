# syntax=docker/dockerfile:1.7

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:11.0-preview
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:11.0-preview

FROM ${SDK_IMAGE} AS build
ARG PROJECT_PATH
ARG CONFIGURATION=Release

WORKDIR /src

COPY global.json ./
COPY backend ./backend

RUN dotnet publish "$PROJECT_PATH" \
    -c "$CONFIGURATION" \
    -o /app/publish \
    /p:UseAppHost=false

FROM ${RUNTIME_IMAGE} AS runtime
ARG PROJECT_DLL

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV APP_DLL=${PROJECT_DLL}

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["sh", "-c", "dotnet \"$APP_DLL\""]
