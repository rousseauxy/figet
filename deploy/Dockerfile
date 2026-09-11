# syntax=docker/dockerfile:1
#
# Build from the repository root:  docker build -f deploy/Dockerfile -t figet .
#
# The runtime image follows OpenShift's restricted security constraints, so it runs unchanged everywhere else:
# non-root, works under an arbitrary UID in group 0, listens on 8080, writes only to /data and /tmp.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/FiGet.Core/FiGet.Core.csproj src/FiGet.Core/
COPY src/FiGet.Http/FiGet.Http.csproj src/FiGet.Http/
COPY src/FiGet.Persistence/FiGet.Persistence.csproj src/FiGet.Persistence/
COPY src/FiGet.Persistence.Sqlite/FiGet.Persistence.Sqlite.csproj src/FiGet.Persistence.Sqlite/
COPY src/FiGet.Persistence.SqlServer/FiGet.Persistence.SqlServer.csproj src/FiGet.Persistence.SqlServer/
COPY src/FiGet.Protocol.V2/FiGet.Protocol.V2.csproj src/FiGet.Protocol.V2/
COPY src/FiGet.Protocol.V3/FiGet.Protocol.V3.csproj src/FiGet.Protocol.V3/
COPY src/FiGet.Storage/FiGet.Storage.csproj src/FiGet.Storage/
COPY src/FiGet.Web/FiGet.Web.csproj src/FiGet.Web/
RUN dotnet restore src/FiGet.Web/FiGet.Web.csproj

COPY src/ src/
RUN dotnet publish src/FiGet.Web/FiGet.Web.csproj --no-restore -c Release -o /app -p:UseAppHost=false

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
