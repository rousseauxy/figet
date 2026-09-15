# FiGet

**Files + Get.** A self-hosted package server for PowerShell and .NET shops: NuGet **v2 and v3**, so Windows PowerShell
5.1 and the newest tooling both work; proxy feeds that cache the PowerShell Gallery and nuget.org for servers without
internet access; asset directories for installers and scripts; and sign-in with the identity provider you already have.

> **Status:** feature-complete for a first release and running on a test instance.
> What was verified, and how, is in [docs/status.md](docs/status.md).

## Why it exists

We started on a self-hosted NuGet.Server: a v2 feed and a drop folder. It served modules, and nothing else. It had no
proxy, so a server without internet access could not install from the PowerShell Gallery. There was no place for the
installers and scripts that go with the modules, no accounts, and it ran on .NET Framework only.

A mixed fleet needs more than that, and all of it at once:

- **Both NuGet protocols, exactly.** Windows PowerShell 5.1 with PowerShellGet 2.x speaks NuGet v2 OData, and most fleets
  are pinned to it. PSResourceGet, dotnet and nuget.exe speak v3. A server that answers one of them almost right breaks
  `Find-Module` or `Install-Module` in ways that are hard to trace.
- **A proxy that tells the truth.** Once part of a module is cached, a proxy must still return one version list with one
  latest version. Otherwise `Update-Module` fails, and a meta-module that pins its dependencies to exact versions, such as
  Microsoft.Graph, cannot install.
- **Files next to packages**, downloadable by a plain `GET`, including folders applications already write to.
- **Sign-in with the existing identity provider**, and rights per feed.
- **One container** that runs on a NAS with SQLite and on OpenShift with SQL Server and several replicas.

The open-source servers we looked at each covered part of that list. FiGet exists to cover all of it, and nothing more.

## What sets it apart

- **Both protocols, recorded from the real clients.** The v2 surface was built from the requests Windows PowerShell 5.1,
  PSResourceGet and nuget.exe actually send, recorded against a working server, and those recordings run as tests. An
  expression FiGet does not understand is refused with an error that names it, never answered with an empty list that
  reads as "not found".
- **Proxy feeds with one version list per package.** Local and upstream versions are merged and sorted before any paging,
  and exactly one is flagged latest. The first upstream in a feed's order that holds a package serves all of it, so two
  different packages that share a name are never mixed. Every upstream version can be fetched on demand, and a pull
  brings its dependencies along.
- **Asset directories.** Installers and scripts by path, with `Range` and `ETag`. Upload by drag and drop, a script, an
  archive, or a URL the server fetches. A directory can also be a folder on the server, such as an existing share, served
  as it is. Downloading and listing can each be opened to anonymous clients separately, and cache headers are set per
  folder.
- **Accounts and sign-in.** Local accounts, and OpenID Connect against any provider (Entra ID, Authentik, Google,
  Keycloak), several at once, configured on the admin pages without a restart. Groups, Read, Publish and Manage per feed,
  provider groups feeding FiGet groups, and personal API keys that never do more than their owner may.
- **Safe to put in front of a fleet.**
  - Nothing uploaded can run as part of the site.
  - Anonymous clients are rate-limited.
  - A feed can be limited to listed networks.
  - Every change is audited with who, what, when and from where.
  - The key ring that protects sessions is encrypted with a master key from a secret; without one, FiGet says so at
    every start.

## At a glance

- NuGet v3: service index, registration, flat container, search, autocomplete, push, delete, symbols
- NuGet v2 OData: the subset the clients send, on both `/nuget/{feed}` and `/nuget/{feed}/api/v2`
- Curated feeds with retention rules, previewed before they run, and pruning of cached copies nobody uses
- Browse and search in the web UI, download any version, per-feed install instructions you can edit
- A usage graph per feed; download counts and last use per version
- A management API for listing versions, finding the latest and deleting, as existing scripts call it
- Audit log with an admin page; health endpoints; OpenTelemetry metrics and traces
- SQLite or SQL Server; local or shared storage; one image, non-root, arbitrary UID

## Tested with

| Client | Protocol |
|---|---|
| Windows PowerShell 5.1 with PowerShellGet 2.2.5 and PackageManagement 1.4.8.1 | v2 (and v3 through PackageManagement's NuGet provider) |
| PowerShell 7 with PSResourceGet | v2 and v3 |
| nuget.exe and the dotnet CLI | v3 |
| Ansible `win_psrepository` and `win_get_url` (built for; not yet run end to end) | v2 and asset directories |
| curl and `Invoke-WebRequest`, including resumed downloads | asset directories |

## Run the image

```
docker run -d --name figet -p 8080:8080 -v figet-data:/data \
  -e FiGet__PublicBaseUrl=http://localhost:8080 \
  -e FiGet__Feeds__0__Name=modules -e FiGet__Feeds__0__AnonymousRead=true \
  ghcr.io/rousseauxy/figet:latest
```

Sign in at http://localhost:8080 as `admin` / `admin`: the first thing FiGet asks is a new password. The container runs
as a non-root, arbitrary UID and keeps its database and files under `/data`. `deploy/compose.example.yml` is the same
thing as a compose file, with SQL Server, shares and a master key as commented lines.

Behind a reverse proxy, with more than one replica, or with secrets and shares to wire in, the settings that matter are
in [docs/configuration.md](docs/configuration.md).

## Run from source

```
dotnet run --project src/FiGet.Web
```

The first start creates a SQLite database under `src/FiGet.Web/data` and listens on http://localhost:5555 (the image
listens on 8080).

## Build and test

Needs the .NET 10 SDK; the version is pinned in `global.json`.

```
dotnet build figet.slnx -c Debug
dotnet test figet.slnx -c Debug
```

The unit tests need nothing. The integration tests run against SQLite by default, and against SQL Server as well when
`FIGET_TEST_SQLSERVER` holds a connection string - both providers must pass before a change to persistence is done:

```
FIGET_TEST_SQLSERVER='Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true' dotnet test figet.slnx -c Debug
```

`tests/FiGet.Compat` holds the scripts that record real clients; they are run by hand on a Windows machine, not in CI.

## Documentation

| Document | What is in it |
|---|---|
| [docs/configuration.md](docs/configuration.md) | Every setting |
| [docs/status.md](docs/status.md) | What was verified, with which client, and when |
| [docs/protocol-v2.md](docs/protocol-v2.md), [protocol-v3.md](docs/protocol-v3.md), [protocol-assets.md](docs/protocol-assets.md), [protocol-management.md](docs/protocol-management.md) | The protocol surfaces and the decisions behind them |
| [docs/auth-plan.md](docs/auth-plan.md) | Accounts, groups, permissions, keys and sign-in |
| [docs/backlog.md](docs/backlog.md) | What is not built, and why |

## Name

"Fi" for files, "Get" because that is what the ecosystem calls a NuGet-compatible server. Yes, it sounds like fidget.

## Licence

MIT. See [LICENSE](LICENSE).
