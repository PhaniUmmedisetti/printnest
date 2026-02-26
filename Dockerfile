# ─────────────────────────────────────────────────────────
# PrintNest API — Dockerfile
# Multi-stage build: build → publish → runtime
# ─────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore — separate layer for caching
COPY printnest.csproj .
RUN dotnet restore printnest.csproj

# Copy everything and build
COPY . .
RUN dotnet publish printnest.csproj -c Release -o /app/publish

# ── Runtime image ─────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root user for security
RUN adduser --disabled-password --gecos "" appuser
USER appuser

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "PrintNest.dll"]
