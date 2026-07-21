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

# Photos, SQLite DB and data-protection keys all live under /data — mount a volume here.
ENV Shoebox__DataPath=/data
RUN mkdir -p /data && chown -R $APP_UID /data
VOLUME /data

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Shoebox.Web.dll"]
