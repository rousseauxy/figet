# syntax=docker/dockerfile:1
#
# Build from the repository root:  docker build -f deploy/Dockerfile -t figet .
#
# The runtime image follows OpenShift's restricted security constraints, so it runs unchanged everywhere else:
# non-root, works under an arbitrary UID in group 0, listens on 8080, writes only to /data and /tmp.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/FiGet.Domain/FiGet.Domain.csproj src/FiGet.Domain/
COPY src/FiGet.Application/FiGet.Application.csproj src/FiGet.Application/
COPY src/FiGet.Infrastructure/FiGet.Infrastructure.csproj src/FiGet.Infrastructure/
COPY src/FiGet.Infrastructure.Sqlite/FiGet.Infrastructure.Sqlite.csproj src/FiGet.Infrastructure.Sqlite/
COPY src/FiGet.Infrastructure.SqlServer/FiGet.Infrastructure.SqlServer.csproj src/FiGet.Infrastructure.SqlServer/
COPY src/FiGet.Http/FiGet.Http.csproj src/FiGet.Http/
COPY src/FiGet.Protocol.V2/FiGet.Protocol.V2.csproj src/FiGet.Protocol.V2/
COPY src/FiGet.Protocol.V3/FiGet.Protocol.V3.csproj src/FiGet.Protocol.V3/
COPY src/FiGet.Web/FiGet.Web.csproj src/FiGet.Web/
RUN dotnet restore src/FiGet.Web/FiGet.Web.csproj

COPY src/ src/

# Deliberately NOT --no-restore, however wasteful that looks next to the restore layer above.
# With it, the publish skips the work that composes static web assets, and
# wwwroot/_framework/blazor.web.js is silently left out of the image. Nothing fails: the build
# succeeds, the app starts, health checks pass. The page then asks for its own framework script,
# gets a 404, no circuit is ever created, and every interactive component renders once as dead
# HTML — a grid that draws itself and then ignores every keystroke. The restore layer is still
# worth keeping: this re-uses it, so the cost of dropping the flag is small.
RUN dotnet publish src/FiGet.Web/FiGet.Web.csproj -c Release -o /app -p:UseAppHost=false

# Fail here rather than shipping that 404. The failure is otherwise invisible until somebody
# signs in and reports that search is broken — there is no error in the log, because nothing
# threw. On failure, list what WAS emitted: the only useful next question is what is in wwwroot.
RUN test -f /app/wwwroot/_framework/blazor.web.js \
    || (echo "FATAL: wwwroot/_framework/blazor.web.js missing from the publish output" \
        && echo "--- /app/wwwroot ---" && ls -laR /app/wwwroot \
        && exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
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
