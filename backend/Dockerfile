# syntax=docker/dockerfile:1
#
# The backend as a container. Build from the repo root, since it needs the shared code and the catalog:
#   docker build -f backend/Dockerfile -t raidzero-backend .
#
# Settings come from environment variables (Backend__TokenSigningKey and so on); see deploy/README.md.
# The SQLite database lives in /data, which should be a volume.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore from the project files alone, so the package layer stays cached until a project file changes.
COPY global.json Directory.Build.props ./
COPY shared/Game.Shared/Game.Shared.csproj shared/Game.Shared/
COPY backend/Game.Backend/Game.Backend.csproj backend/Game.Backend/
RUN dotnet restore backend/Game.Backend/Game.Backend.csproj

COPY shared/ shared/
COPY backend/ backend/
RUN dotnet publish backend/Game.Backend/Game.Backend.csproj --no-restore --configuration Release --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /app ./
COPY shared/catalog/item_catalog.json catalog/item_catalog.json

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Backend__DatabasePath=/data/raidzero.db \
    Backend__CatalogPath=/app/catalog/item_catalog.json \
    Backend__StarterKitPath=/app/starter_kit.json

# The image's built-in non-root user owns the data folder; the app never runs as root.
RUN mkdir /data && chown "$APP_UID" /data
VOLUME /data
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "Game.Backend.dll"]
