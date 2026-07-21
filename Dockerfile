# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Shoebox.Web/Shoebox.Web.csproj Shoebox.Web/
RUN dotnet restore Shoebox.Web/Shoebox.Web.csproj

COPY src/Shoebox.Web/ Shoebox.Web/
RUN dotnet publish Shoebox.Web/Shoebox.Web.csproj -c Release -o /app/publish

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# su-exec: lightweight privilege-drop tool (replaces gosu, ships in Debian repos)
RUN apt-get update && apt-get install -y --no-install-recommends su-exec && rm -rf /var/lib/apt/lists/*

# Photos, SQLite DB and data-protection keys all live under /data — mount a volume here.
ENV Shoebox__DataPath=/data
# Pre-create /data/keys so named volumes pick up the directory on first initialisation.
RUN mkdir -p /data/keys && chown -R $APP_UID /data
VOLUME /data

COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

EXPOSE 8080
# Entrypoint runs as root to fix /data ownership (handles bind mounts and
# pre-existing volumes), then drops to APP_UID before starting the app.
ENTRYPOINT ["/docker-entrypoint.sh"]
