# syntax=docker/dockerfile:1
#
# Build from the repository root:  docker build -f deploy/Dockerfile -t figet .
#
# The runtime image follows OpenShift's restricted security constraints, so it runs unchanged everywhere else:
# non-root, works under an arbitrary UID in group 0, listens on 8080, writes only to /data and /tmp.

# Both stages pinned to exact patches, and to the same runtime: the SDK composes the static web assets the runtime serves, and
# two floating tags that agree today need not agree tomorrow - a mismatch fails silently, with a healthy container and a 404
# for a file a page asks for. Bump both together: the SDK patch whose bundled runtime is the aspnet tag below
# (`docker run --rm --entrypoint dotnet mcr.microsoft.com/dotnet/sdk:<tag> --list-runtimes`). A runtime patch release can
# carry security fixes FiGet needs without a code change, so check for one monthly.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/FiGet.Domain/FiGet.Domain.csproj src/FiGet.Domain/
COPY src/FiGet.Application/FiGet.Application.csproj src/FiGet.Application/
COPY src/FiGet.Infrastructure/FiGet.Infrastructure.csproj src/FiGet.Infrastructure/
COPY src/FiGet.Infrastructure.Sqlite/FiGet.Infrastructure.Sqlite.csproj src/FiGet.Infrastructure.Sqlite/
COPY src/FiGet.Infrastructure.SqlServer/FiGet.Infrastructure.SqlServer.csproj src/FiGet.Infrastructure.SqlServer/
COPY src/FiGet.Http/FiGet.Http.csproj src/FiGet.Http/
COPY src/FiGet.Protocol.Assets/FiGet.Protocol.Assets.csproj src/FiGet.Protocol.Assets/
COPY src/FiGet.Protocol.Management/FiGet.Protocol.Management.csproj src/FiGet.Protocol.Management/
COPY src/FiGet.Protocol.V2/FiGet.Protocol.V2.csproj src/FiGet.Protocol.V2/
COPY src/FiGet.Protocol.V3/FiGet.Protocol.V3.csproj src/FiGet.Protocol.V3/
COPY src/FiGet.Web/FiGet.Web.csproj src/FiGet.Web/
RUN dotnet restore src/FiGet.Web/FiGet.Web.csproj

COPY src/ src/

# What this build is, stamped into the assembly so the server can say it: the pages show it, and a support question that
# starts with "which version?" should not need the image tag the operator may no longer remember pulling.
#
# The default is the SDK's own, which this server reads as "nobody said" - so a local build reports no version rather
# than inventing one, and only a release build that passes the tag claims to be a release.
ARG VERSION=1.0.0

# Deliberately NOT --no-restore, however wasteful that looks next to the restore layer above.
# With it, the publish skipped composing static web assets: on 2026-09-12 the framework script was
# missing from the image while the app started and passed its health checks. That script is gone since
# the pages became fully static, but the stylesheet and script are served through the same composed
# manifest. The restore layer is still worth keeping: this re-uses it.
RUN dotnet publish src/FiGet.Web/FiGet.Web.csproj -c Release -o /app -p:UseAppHost=false \
    -p:Version="$VERSION" -p:InformationalVersion="$VERSION"

# Fail here rather than shipping that: nothing throws, so nothing in the log would say so.
RUN test -f /app/FiGet.Web.staticwebassets.endpoints.json && test -f /app/wwwroot/app.css \
    || (echo "FATAL: the static web assets manifest or app.css is missing from the publish output" \
        && echo "--- /app/wwwroot ---" && ls -laR /app/wwwroot \
        && exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    FiGet__Storage__Root=/data \
    HOME=/tmp

# Group 0 owns /data and the group has the owner's rights: OpenShift runs the container as a random UID in group 0.
RUN mkdir -p /data && chown -R "$APP_UID":0 /data && chmod -R g=u /data

COPY --from=build /app .

USER $APP_UID
VOLUME /data
EXPOSE 8080

ENTRYPOINT ["dotnet", "FiGet.Web.dll"]
