# syntax=docker/dockerfile:1

# ThixClip.Cloud - the server of ThixClip (sync API + web app). The desktop app is not part of the image.

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
# The build runs on the builder's own platform: the published app is portable (no native host), so the arm64 image needs no emulation here.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src

# Restore first (better layer caching).
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/ThixClip.Shared/ThixClip.Shared.csproj", "src/ThixClip.Shared/"]
COPY ["src/ThixClip.Cloud/ThixClip.Cloud.csproj", "src/ThixClip.Cloud/"]
RUN dotnet restore "src/ThixClip.Cloud/ThixClip.Cloud.csproj"

COPY ["src/ThixClip.Shared", "src/ThixClip.Shared"]
COPY ["src/ThixClip.Cloud", "src/ThixClip.Cloud"]
# Fall back to today's date when BuildDate is not supplied, so the version never ends in "-".
RUN BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    dotnet publish "src/ThixClip.Cloud/ThixClip.Cloud.csproj" \
    -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false \
    -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE

# ---------------------------------------------------------------------------
# Runtime stage (ASP.NET Core base image)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

LABEL org.opencontainers.image.title="ThixClip.Cloud" \
      org.opencontainers.image.description="ThixClip.Cloud - der Zwischenablage-Verlauf aller Geräte: Sync-Server und Web-App (PWA)" \
      org.opencontainers.image.source="https://github.com/Real-TTX/ThixClip"

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:4950 \
    HTTP_PORTS="" \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    TZ=Europe/Berlin

EXPOSE 4950

COPY --from=build /app/publish .

# One volume for what the app keeps in files: the keys that protect the sessions. The database is the other container.
RUN mkdir -p /data/keys && chown -R $APP_UID:$APP_UID /data
VOLUME /data
USER $APP_UID

# The app checks itself on /health (and its database), so the image needs no curl.
HEALTHCHECK --interval=30s --timeout=8s --start-period=30s --retries=3 \
    CMD ["dotnet", "ThixClip.Cloud.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "ThixClip.Cloud.dll"]
