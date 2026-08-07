# syntax=docker/dockerfile:1

# ── build ──────────────────────────────────────────────────────────────────────
# The csproj is copied on its own first so that `restore` is cached and only re-runs
# when a dependency actually changes, not on every source edit.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY src/PosLedger.Api/PosLedger.Api.csproj src/PosLedger.Api/
RUN dotnet restore src/PosLedger.Api/PosLedger.Api.csproj

COPY src/ src/
RUN dotnet publish src/PosLedger.Api/PosLedger.Api.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# ── runtime ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final

# Alpine ships without ICU, and .NET refuses to start with globalization enabled and no
# ICU present. Running in invariant mode instead would be the smaller image and the wrong
# trade: this API formats currency and parses dates for a Colombian client, and invariant
# mode silently changes both. ~30MB is the correct price for that.
RUN apk add --no-cache icu-libs tzdata

# Runs as an unprivileged user. A container that runs as root is one container
# escape away from being a host compromise, and there is no reason for it here.
RUN addgroup -S posledger && adduser -S -G posledger -H posledger

WORKDIR /app
COPY --from=build --chown=posledger:posledger /app/publish .

USER posledger

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_gcServer=0

EXPOSE 8080

# Liveness only — /ready would make a database blip look like a dead container.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD wget --quiet --tries=1 --spider http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "PosLedger.Api.dll"]
