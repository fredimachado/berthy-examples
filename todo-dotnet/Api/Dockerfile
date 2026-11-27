# Build on the machine's own architecture and publish for TARGETARCH, so a
# multi-arch image needs no QEMU for the .NET compile. The SPA is built once
# (arch-independent) then copied into the published output.
FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS frontend
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Api/Todo.Api.csproj Api/
RUN dotnet restore Api/Todo.Api.csproj -a $TARGETARCH
COPY Api/ Api/
# Frontend already built; SkipFrontendBuild avoids a second npm run in this stage.
RUN dotnet publish Api/Todo.Api.csproj -c Release -a $TARGETARCH -o /app/publish \
    --no-restore -p:SkipFrontendBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:11.0

# This label links the GitHub Container Registry package to the repository.
LABEL org.opencontainers.image.source="https://github.com/fredimachado/berthy-examples" \
      org.opencontainers.image.description="Todo API for trying Berthy: ASP.NET Core, Postgres and Redis"

ARG VERSION=dev
ARG COMMIT=unknown
ENV BuildVersion=$VERSION \
    BuildCommit=$COMMIT \
    ASPNETCORE_URLS=http://+:8080

WORKDIR /app
COPY --from=build /app/publish .
COPY --from=frontend /web/dist ./wwwroot

# Non-root: aspnet sets APP_UID (1654) but leaves User empty unless we set it.
USER $APP_UID
EXPOSE 8080

# ENTRYPOINT so `docker run IMAGE migrate` and `docker run IMAGE version` work.
# (Berthy for the Go image replaces CMD with a binary path; this image is not
# wired to Berthy yet.)
ENTRYPOINT ["dotnet", "Todo.Api.dll"]
