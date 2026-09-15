# FiGet — Files + Get

A self-hosted package server for PowerShell and .NET shops: NuGet **v2 and v3**, so Windows PowerShell 5.1 and the newest
tooling both work; proxy feeds that cache the PowerShell Gallery, nuget.org and the Chocolatey community repository for
servers without internet access; asset
directories for installers and scripts; and sign-in with the identity provider you already have.

> **Status 2026-09-16:** released as 1.0.0, MIT licence. The image is `ghcr.io/rousseauxy/figet:1.0.0` (also
> `:latest`), for `linux/amd64` and `linux/arm64`.
>
> This is the user guide. [README.md](../../README.md) is the short version, `docs/` beside it holds the build plan,
> the protocol notes and the configuration reference.

## Why we built it

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

### Both protocols, recorded from the real clients

The v2 surface was built from the requests Windows PowerShell 5.1, PSResourceGet and nuget.exe actually send, recorded
against a working server, and those recordings run as tests. An expression FiGet does not understand is refused with an
error that names it, never answered with an empty list that reads as "package not found".

### Proxy feeds with one version list per package

Local and upstream versions are merged and sorted before any paging, and exactly one is flagged latest, however much is
cached. The first upstream in a feed's order that holds a package serves all of it, so two different packages that share
a name are never mixed. Every upstream version can be fetched on demand, and pulling a package into a feed brings its
dependencies along, so the offline server it is for can install it.

### A feed for each kind of package

A feed is created for PowerShell modules, NuGet packages, Chocolatey packages or any client. The choice sets the connect
and install commands its pages show, can add the matching public gallery as its upstream, and decides which pushes it
takes, judged from the files inside the package:

| Used for | Takes | Refuses |
|---|---|---|
| PowerShell modules | a module (`{id}.psd1` at the root) or a script (`{id}.ps1`) | everything else |
| NuGet packages | .NET packages and anything it cannot tell apart | modules, Chocolatey packages (`chocolateyInstall.ps1`) |
| Chocolatey packages | Chocolatey packages, portable and meta packages | modules, .NET packages (`lib/`, `ref/`, analyzers, dotnet tools) |
| Any client | everything | nothing |

A refused push answers 400 with what was found. Reading is never limited, and copies from an upstream are not checked.

### Files next to packages

Installers and scripts by path, with `Range` for resumed downloads and `ETag` for caches. Files go in by drag and drop,
from a script, as an archive unpacked into a folder, or fetched by the server from a URL. A directory can also be a
folder on the server, such as an existing share applications write to, served as it is. Downloading and listing can
each be opened to anonymous clients separately, and cache headers are set per folder.
[More on asset directories](asset-directories.md).

### Your identity provider

Local accounts, and OpenID Connect against any provider (Microsoft Entra ID, Authentik, Google, Keycloak), several at
once, configured on the admin pages without a restart. Groups, and Read, Publish and Manage per feed, with provider groups
feeding FiGet groups. Personal API keys never do more than their owner may.

### Safe to put in front of a fleet

- Nothing uploaded can run as part of the site.
- Anonymous clients are rate-limited.
- A feed can be limited to listed networks.
- Every change is audited with who, what, when and from where.
- The key ring that protects sessions is encrypted with a master key from a secret.
- Where an upstream points, and with which credential, is an administrator's setting.

## At a glance

- NuGet v3: service index, registration, flat container, search, autocomplete, push, delete, symbols
- NuGet v2 OData: the subset the clients send, on both `/nuget/{feed}` and `/nuget/{feed}/api/v2`
- Feeds for PowerShell modules, NuGet packages or Chocolatey packages, each refusing the other kinds on push
- Curated feeds with retention rules, previewed before they run, and pruning of cached copies nobody uses
- Browse and search in the web UI, download any version, per-feed install instructions you can edit
- A usage graph per feed; download counts and last use per version
- A management API for listing versions, finding the latest and deleting, in the shape existing scripts call
- An audit log with an admin page; health endpoints; OpenTelemetry metrics and traces
- The same URL shapes a fleet already points at: `/nuget/{feed}/`, `/nuget/{feed}/v3/index.json`,
  `/endpoints/{directory}/content/{path}`

## Tested with

| Client | Protocol |
|---|---|
| Windows PowerShell 5.1 with PowerShellGet 2.2.5 and PackageManagement 1.4.8.1 | v2 (and v3 through PackageManagement's NuGet provider) |
| PowerShell 7 with PSResourceGet | v2 and v3; register it at `/nuget/{feed}/api/v2`, where exact versions, wildcards and tags work ([details](powershell-clients.md)) |
| nuget.exe and the dotnet CLI | v3 |
| Ansible `win_psrepository` and `win_get_url` (built for; not yet run end to end) | v2 and asset directories |
| Chocolatey 2.7.4: search, install, outdated, upgrade | v2 |
| curl and `Invoke-WebRequest`, including resumed downloads | asset directories |

## Where it runs

One container image, built for the strictest target and therefore fine everywhere else:

- **A single container** with SQLite and a local volume: a NAS, a small VM, a developer machine.
- **Kubernetes or OpenShift**: several replicas, SQL Server, and a shared volume. The image runs as a non-root, arbitrary
  UID on port 8080.

[Running in production](running-in-production.md) lists the settings that matter behind a proxy and with replicas.

## Guides

- [Asset directories](asset-directories.md): FiGet's own storage or a folder on the server, who may download and list,
  allowed networks and cache modes.
- [Running in production](running-in-production.md): proxies, replicas, secrets and storage.
- [Sign-in with Microsoft Entra ID](sign-in-entra-id.md): app registration, app roles mapped to FiGet groups, and
  the provider settings in FiGet.
- [Sign-in with Authentik](sign-in-authentik.md): application and provider, a group binding, Authentik groups mapped
  to FiGet groups, and the settings that break sign-in when missed.

## Name

**FiGet**: "Fi" for files, "Get" because that is what the ecosystem calls a NuGet-compatible server. It sounds like
fidget, and that is fine.

